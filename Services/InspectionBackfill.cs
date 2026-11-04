using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Infrastructure.Crawler;
using MovieHub.Infrastructure.Media;
using MovieHub.Models;

namespace MovieHub.Services;

/// <summary>What the player needs to know about a file's streams.</summary>
public sealed record InspectionSnapshot(
    bool Inspected, string? Error, double? Duration, string? VideoCodec, string? AudioCodec,
    IReadOnlyList<InspectionAudio> Audio, int EmbeddedSubtitles, int? PreferredAudio, int? DefaultAudio);

public sealed record InspectionAudio(int Index, string? Lang, string? Title, string? Codec, int? Channels, bool IsDefault);

/// <summary>
/// Keeps stream information complete. Files whose ffprobe run failed (tool missing at scan time, server briefly offline) or never ran
/// are inspected again: one file on demand when a viewer opens the player, or all of them in the background from the admin dashboard.
/// </summary>
public sealed class InspectionBackfill
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly MediaInspectionService _inspect;
    private readonly SourcePolicy _policy;
    private readonly ILogger<InspectionBackfill> _log;
    private readonly CancellationToken _stopping;
    private readonly ConcurrentDictionary<int, DateTime> _lastTried = new();
    private int _running;

    public InspectionBackfill(IDbContextFactory<AppDbContext> factory, MediaInspectionService inspect, SourcePolicy policy,
        ILogger<InspectionBackfill> log, IHostApplicationLifetime lifetime)
    {
        _factory = factory; _inspect = inspect; _policy = policy; _log = log; _stopping = lifetime.ApplicationStopping;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public int Total { get; private set; }
    public int Done { get; private set; }
    public int Failed { get; private set; }

    public static IQueryable<MediaFile> Pending(IQueryable<MediaFile> q) => q.Where(f => f.InspectedAt == null || f.InspectionError != null);

    public static InspectionSnapshot Snapshot(MediaFile f)
    {
        var audio = f.Streams.Where(s => s.StreamType == StreamKind.Audio).OrderBy(s => s.StreamIndex)
            .Select(s => new InspectionAudio(s.StreamIndex, s.Language, s.Title, s.Codec, s.Channels, s.IsDefault)).ToList();
        var pick = AudioPreference.Choose(audio, a => a.Lang, a => a.Title, a => a.IsDefault);
        var def = audio.FindIndex(a => a.IsDefault); if (def < 0 && audio.Count > 0) def = 0;
        return new InspectionSnapshot(f.InspectedAt != null && f.InspectionError == null, f.InspectionError, f.Duration, f.VideoCodec, f.AudioCodec,
            audio, f.Streams.Count(s => s.StreamType == StreamKind.Subtitle),
            pick >= 0 ? audio[pick].Index : null, def >= 0 ? audio[def].Index : null);
    }

    /// <summary>Inspects one file now unless it was tried within the last 90 seconds (the answer then comes from the database).</summary>
    public async Task<InspectionSnapshot?> InspectOneAsync(int fileId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var file = await db.MediaFiles.Include(f => f.Streams).FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file == null) return null;

        var needs = file.InspectedAt == null || file.InspectionError != null;
        var now = DateTime.UtcNow;
        if (!needs || !_policy.IsAllowed(file.FileUrl)) return Snapshot(file);
        if (_lastTried.TryGetValue(fileId, out var last) && now - last < TimeSpan.FromSeconds(90)) return Snapshot(file);
        _lastTried[fileId] = now;

        await InspectAndSaveAsync(db, file, ct);
        return Snapshot(file);
    }

    private async Task InspectAndSaveAsync(AppDbContext db, MediaFile file, CancellationToken ct)
    {
        try
        {
            await _inspect.InspectAsync(file, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            file.InspectedAt = DateTime.UtcNow;
            file.InspectionError = ex.Message;
            _log.LogWarning(ex, "Inspection of {File} failed.", file.FileName);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Starts a background pass over every pending file. Returns false when one is already running.</summary>
    public bool TryStartAll()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        Done = 0; Failed = 0; Total = 0; _doneCounter = 0; _failedCounter = 0;
        _ = Task.Run(RunAllAsync);
        return true;
    }

    private async Task RunAllAsync()
    {
        try
        {
            List<int> ids;
            await using (var db = await _factory.CreateDbContextAsync(_stopping))
                ids = await Pending(db.MediaFiles.AsNoTracking()).OrderBy(f => f.Id).Select(f => f.Id).ToListAsync(_stopping);
            Total = ids.Count;

            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = _stopping }, async (id, ct) =>
            {
                try
                {
                    await using var db = await _factory.CreateDbContextAsync(ct);
                    var file = await db.MediaFiles.Include(f => f.Streams).FirstOrDefaultAsync(f => f.Id == id, ct);
                    if (file != null && _policy.IsAllowed(file.FileUrl))
                    {
                        await InspectAndSaveAsync(db, file, ct);
                        if (file.InspectionError != null) Interlocked.Increment(ref _failedCounter);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Background inspection of file {Id} failed.", id); Interlocked.Increment(ref _failedCounter); }
                finally { Interlocked.Increment(ref _doneCounter); Done = _doneCounter; Failed = _failedCounter; }
            });
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { _log.LogError(ex, "Background inspection pass failed."); }
        finally { Volatile.Write(ref _running, 0); }
    }

    private int _doneCounter, _failedCounter;
}
