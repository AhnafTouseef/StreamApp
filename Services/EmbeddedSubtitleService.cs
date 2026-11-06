using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;
using MovieHub.Infrastructure.Media;
using MovieHub.Models;

namespace MovieHub.Services;

public sealed record WindowTrack(int Index, string? Language, string? Title, string? Codec, bool IsDefault, bool Supported, string? Vtt);
public sealed record SubtitleWindow(double From, double Length, IReadOnlyList<WindowTrack> Tracks);

public sealed record EmbeddedTrack(int Index, string? Language, string? Title, string? Codec, bool IsDefault, bool Supported, string State);

/// <summary>
/// Browsers cannot show subtitle tracks that live inside a video file. This service lets ffmpeg read the file once, convert every text-based
/// subtitle track to WebVTT and hold the result for a few minutes so the player can fetch it. The player then keeps the subtitles in the browser's own
/// storage (IndexedDB), so the server never accumulates subtitle files: anything staged here is deleted after <see cref="MediaOptions.SubtitleStagingMinutes"/>.
/// Image-based tracks (Blu-ray PGS, DVD VobSub) cannot be converted to text and are reported as unsupported.
/// Extraction runs in the background (the whole file has to be read once, because subtitles are interleaved with the video); the player polls
/// <see cref="Describe"/> until the tracks are ready. Extraction is limited to a few at a time and each file is only ever extracted once.
/// </summary>
public sealed class EmbeddedSubtitleService
{
    // ffmpeg can turn these into WebVTT. PGS / VobSub / DVB are pictures, not text.
    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text" };

    private readonly MediaOptions _opt;
    private readonly FfmpegTool _ffmpeg;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<EmbeddedSubtitleService> _log;
    private readonly string _dir;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _windowSlots = new(2);
    private readonly ConcurrentDictionary<int, Job> _jobs = new();
    private readonly object _gate = new();

    private sealed class Job { public volatile bool Done; public string? Error; public DateTime FinishedAt; }

    public EmbeddedSubtitleService(IOptions<MediaOptions> opt, FfmpegTool ffmpeg, IHostEnvironment env, IHostApplicationLifetime lifetime, ILogger<EmbeddedSubtitleService> log)
    {
        _opt = opt.Value; _ffmpeg = ffmpeg; _lifetime = lifetime; _log = log;
        var dir = string.IsNullOrWhiteSpace(_opt.SubtitleStagingDirectory) ? Path.Combine(Path.GetTempPath(), "moviehub-subtitles") : _opt.SubtitleStagingDirectory;
        _dir = Path.IsPathRooted(dir) ? dir : Path.Combine(env.ContentRootPath, dir);
        _slots = new SemaphoreSlim(Math.Max(1, _opt.MaxSubtitleExtractions));
        Purge(_dir, TimeSpan.Zero, DateTime.UtcNow);   // nothing from an earlier run is worth keeping: browsers hold the real copies
    }

    private DateTime _lastPurge = DateTime.MinValue;

    /// <summary>Deletes staged files older than <paramref name="ttl"/>. Returns how many were removed.</summary>
    public static int Purge(string dir, TimeSpan ttl, DateTime nowUtc)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try
                {
                    if (nowUtc - File.GetLastWriteTimeUtc(f) >= ttl) { File.Delete(f); removed++; }
                }
                catch (IOException) { /* in use or already gone */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        return removed;
    }
    private void PurgeExpired()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPurge < TimeSpan.FromMinutes(1)) return;
        _lastPurge = now;
        Purge(_dir, TimeSpan.FromMinutes(Math.Max(1, _opt.SubtitleStagingMinutes)), now);
    }

    public static bool IsText(string? codec) => codec != null && TextCodecs.Contains(codec);

    /// <summary>Cache file for one track. The URL hash keeps a changed or replaced file from being served stale subtitles.</summary>
    public string PathFor(MediaFileInfo file, int streamIndex)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(file.Url)))[..10].ToLowerInvariant();
        return Path.Combine(_dir, $"{file.Id}-{hash}-{streamIndex}.vtt");
    }

    /// <summary>The subtitle tracks of a file with their current state: ready, preparing, failed, unsupported or unavailable. Starts extraction when needed.</summary>
    public IReadOnlyList<EmbeddedTrack> Describe(MediaFileInfo file, bool start = true)
    {
        PurgeExpired();
        var subs = file.Streams.Where(s => s.Kind == StreamKind.Subtitle).OrderBy(s => s.Index).ToList();
        var missing = subs.Where(s => IsText(s.Codec) && !File.Exists(PathFor(file, s.Index))).Select(s => s.Index).ToList();
        if (start && missing.Count > 0 && _ffmpeg.IsAvailable) EnsureStarted(file, missing);

        _jobs.TryGetValue(file.Id, out var job);
        var failed = job is { Done: true, Error: not null };
        return subs.Select(s =>
        {
            string state;
            if (!IsText(s.Codec)) state = "unsupported";
            else if (File.Exists(PathFor(file, s.Index))) state = "ready";
            else if (!_ffmpeg.IsAvailable) state = "unavailable";
            else if (failed) state = "failed";
            else state = "preparing";
            return new EmbeddedTrack(s.Index, s.Language, s.Title, s.Codec, s.IsDefault, IsText(s.Codec), state);
        }).ToList();
    }

    /// <summary>
    /// The subtitle cues of just one stretch of the movie (<paramref name="length"/> seconds from <paramref name="from"/>), read by jumping straight to that position
    /// in the file instead of reading all of it. This is what makes subtitles appear within seconds, like in a desktop player; the full extraction fills in the rest.
    /// Cue times in the result are relative to <c>From</c>. Nothing is kept on the server.
    /// </summary>
    public async Task<SubtitleWindow> ReadWindowAsync(MediaFileInfo file, double from, double length, CancellationToken ct)
    {
        from = Math.Max(0, from); length = Math.Clamp(length, 30, 900);
        var subs = file.Streams.Where(s => s.Kind == StreamKind.Subtitle).OrderBy(s => s.Index).ToList();
        var text = subs.Where(s => IsText(s.Codec)).ToList();
        var vtts = new Dictionary<int, string>();
        if (text.Count > 0 && _ffmpeg.IsAvailable)
        {
            await _windowSlots.WaitAsync(ct);
            var temps = new List<(int Index, string Path)>();
            try
            {
                Directory.CreateDirectory(_dir);
                var psi = new ProcessStartInfo(_ffmpeg.Path!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "http,https,tcp,tls,crypto",
                                          "-ss", from.ToString("0.###", inv), "-i", file.Url, "-t", length.ToString("0.###", inv) })
                    psi.ArgumentList.Add(a);
                foreach (var t in text)
                {
                    var tmp = Path.Combine(_dir, $"w-{Guid.NewGuid():N}-{t.Index}.tmp");
                    temps.Add((t.Index, tmp));
                    foreach (var a in new[] { "-map", $"0:{t.Index}", "-c:s", "webvtt", "-f", "webvtt", tmp }) psi.ArgumentList.Add(a);
                }
                using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
                var stderr = proc.StandardError.ReadToEndAsync();
                _ = proc.StandardOutput.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                try { await proc.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { try { proc.Kill(true); } catch (InvalidOperationException) { } throw; }
                if (proc.ExitCode != 0)
                {
                    var err = (await stderr).Trim();
                    throw new InvalidOperationException(string.IsNullOrEmpty(err) ? $"ffmpeg exited with code {proc.ExitCode}." : err.Split('\n')[0]);
                }
                foreach (var (index, path) in temps)
                    if (File.Exists(path)) vtts[index] = await File.ReadAllTextAsync(path, ct);
            }
            finally
            {
                foreach (var (_, path) in temps) { try { File.Delete(path); } catch (IOException) { } }
                _windowSlots.Release();
            }
        }
        var tracks = subs.Select(s => new WindowTrack(s.Index, s.Language, s.Title, s.Codec, s.IsDefault, IsText(s.Codec), vtts.GetValueOrDefault(s.Index))).ToList();
        return new SubtitleWindow(from, length, tracks);
    }

    private void EnsureStarted(MediaFileInfo file, List<int> missing)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(file.Id, out var existing))
            {
                if (!existing.Done) return;                                                           // already working on it
                if (existing.Error != null && DateTime.UtcNow - existing.FinishedAt < TimeSpan.FromMinutes(2)) return; // do not hammer a failing file
            }
            var job = new Job();
            _jobs[file.Id] = job;
            _ = Task.Run(() => ExtractAsync(file, missing, job));
        }
    }

    private async Task ExtractAsync(MediaFileInfo file, List<int> indexes, Job job)
    {
        var ct = _lifetime.ApplicationStopping;
        var held = false;
        var parts = new List<(string Part, string Final)>();
        try
        {
            await _slots.WaitAsync(ct);
            held = true;
            Directory.CreateDirectory(_dir);

            var psi = new ProcessStartInfo(_ffmpeg.Path!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            // The protocol whitelist stops a redirect from making ffmpeg read local files or other protocols.
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "http,https,tcp,tls,crypto", "-i", file.Url })
                psi.ArgumentList.Add(a);
            foreach (var index in indexes)
            {
                var final = PathFor(file, index);
                var part = final + ".part";
                try { File.Delete(part); } catch (IOException) { }
                parts.Add((part, final));
                foreach (var a in new[] { "-map", $"0:{index}", "-c:s", "webvtt", "-f", "webvtt", part }) psi.ArgumentList.Add(a);
            }

            _log.LogInformation("Extracting {Count} embedded subtitle track(s) of file {FileId} (reads the whole file once).", indexes.Count, file.Id);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
            var stderr = proc.StandardError.ReadToEndAsync();
            _ = proc.StandardOutput.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _opt.SubtitleExtractTimeoutMinutes)));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch (InvalidOperationException) { }
                throw;
            }
            if (proc.ExitCode != 0)
            {
                var err = (await stderr).Trim();
                throw new InvalidOperationException(string.IsNullOrEmpty(err) ? $"ffmpeg exited with code {proc.ExitCode}." : err.Split('\n')[0]);
            }
            foreach (var (part, final) in parts)
                if (File.Exists(part)) File.Move(part, final, overwrite: true);
            _log.LogInformation("Embedded subtitles of file {FileId} are ready for the browser to fetch.", file.Id);
        }
        catch (OperationCanceledException) { job.Error = "Subtitle extraction took too long or the application is stopping."; }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            _log.LogWarning("Could not extract the embedded subtitles of file {FileId}: {Message}", file.Id, ex.Message);
        }
        finally
        {
            foreach (var (part, _) in parts) { try { File.Delete(part); } catch (IOException) { } }
            if (held) _slots.Release();
            job.FinishedAt = DateTime.UtcNow;
            job.Done = true;
        }
    }
}
