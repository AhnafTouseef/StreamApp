using System.IO.Compression;
using Microsoft.Extensions.Options;
using StreamApp.Configuration;
using StreamApp.Infrastructure.Media;

namespace StreamApp.Services;

/// <summary>
/// Downloads a Windows FFmpeg build and puts ffmpeg.exe + ffprobe.exe into the project's tools folder, so the project folder is self-contained.
/// The URL comes from configuration only (never from a request). Only the two executables are extracted, each to a fixed file name.
/// </summary>
public sealed class FfmpegInstaller
{
    public const int MaxDownloadBytes = 400 * 1024 * 1024;

    private readonly IHttpClientFactory _http;
    private readonly MediaOptions _opt;
    private readonly ToolDirectories _dirs;
    private readonly FfmpegTool _ffmpeg;
    private readonly FfprobeRunner _ffprobe;
    private readonly ILogger<FfmpegInstaller> _log;
    private readonly object _gate = new();
    private int _running;

    public const string HttpClientName = "ffmpeg-download";

    public FfmpegInstaller(IHttpClientFactory http, IOptions<MediaOptions> opt, ToolDirectories dirs, FfmpegTool ffmpeg, FfprobeRunner ffprobe, ILogger<FfmpegInstaller> log)
    { _http = http; _opt = opt.Value; _dirs = dirs; _ffmpeg = ffmpeg; _ffprobe = ffprobe; _log = log; }

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public string Status { get; private set; } = "";
    public string? LastError { get; private set; }
    public string TargetDirectory => _dirs.Primary;

    /// <summary>The download is built for Windows; elsewhere an explicit URL override is required to enable it.</summary>
    public bool CanInstall => IsUrlAcceptable(_opt.FfmpegDownloadUrl) && (OperatingSystem.IsWindows() || !IsDefaultUrl());
    private bool IsDefaultUrl() => string.Equals(_opt.FfmpegDownloadUrl, new MediaOptions().FfmpegDownloadUrl, StringComparison.Ordinal);

    public static bool IsUrlAcceptable(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme == Uri.UriSchemeHttps) return true;
        return u.Scheme == Uri.UriSchemeHttp && u.IsLoopback; // loopback http: local mirrors and tests only
    }

    public bool TryStart()
    {
        if (!CanInstall) return false;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        LastError = null;
        Status = "Starting…";
        _ = Task.Run(async () =>
        {
            try { await InstallAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Status = "Failed";
                _log.LogWarning(ex, "FFmpeg download failed.");
            }
            finally { Volatile.Write(ref _running, 0); }
        });
        return true;
    }

    public async Task InstallAsync(CancellationToken ct)
    {
        var target = _dirs.Primary;
        Directory.CreateDirectory(target);
        var tmp = Path.Combine(target, "download-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Status = "Downloading…";
            using var client = _http.CreateClient(HttpClientName);
            using var resp = await client.GetAsync(_opt.FfmpegDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is long len && len > MaxDownloadBytes)
                throw new InvalidOperationException("The download is larger than the 400 MB limit.");

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > MaxDownloadBytes) throw new InvalidOperationException("The download is larger than the 400 MB limit.");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    Status = $"Downloading… {total / (1024 * 1024)} MB";
                }
            }

            Status = "Extracting…";
            var got = new List<string>();
            using (var zip = ZipFile.OpenRead(tmp))
            {
                foreach (var name in new[] { "ffmpeg", "ffprobe" })
                {
                    var exe = ToolLocator.ExeName(name);
                    // Match by file name only, preferring a "bin/" entry; the entry's own path is never used for writing.
                    var entry = zip.Entries
                        .Where(e => string.Equals(Path.GetFileName(e.FullName), exe, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(e => e.FullName.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();
                    if (entry == null) throw new InvalidOperationException($"The download does not contain {exe}.");
                    var dest = Path.Combine(target, exe);
                    var part = dest + ".part";
                    entry.ExtractToFile(part, overwrite: true);
                    File.Move(part, dest, overwrite: true);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    got.Add(exe);
                }
            }

            Status = "Checking…";
            _ffmpeg.Reset();
            _ffprobe.Reset();
            if (!_ffmpeg.IsAvailable || !_ffprobe.IsAvailable)
                throw new InvalidOperationException("The files were extracted but could not be run (" + string.Join(", ", got) + "). Antivirus may have blocked them.");
            Status = "Installed";
            _log.LogInformation("FFmpeg installed into {Dir}.", target);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { /* best effort */ }
        }
    }
}
