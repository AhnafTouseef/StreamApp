using Microsoft.EntityFrameworkCore;
using StreamApp.Data;
using StreamApp.Infrastructure.Metadata;

namespace StreamApp.Services;

/// <summary>
/// Switches every TMDB-matched movie that still shows the poster from the ISP folder over to its TMDB poster.
/// One click on the dashboard starts it; it runs in the background until nothing is left (no repeated clicking).
/// </summary>
public sealed class PosterBackfill
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly MetadataService _metadata;
    private readonly ILogger<PosterBackfill> _log;
    private readonly CancellationToken _stopping;
    private int _running, _done, _switched, _kept, _failed;

    public PosterBackfill(IDbContextFactory<AppDbContext> factory, MetadataService metadata, ILogger<PosterBackfill> log, IHostApplicationLifetime lifetime)
    { _factory = factory; _metadata = metadata; _log = log; _stopping = lifetime.ApplicationStopping; }

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public int Total { get; private set; }
    public int Done => Volatile.Read(ref _done);
    public int Switched => Volatile.Read(ref _switched);
    /// <summary>Movies TMDB has no poster for; they keep the folder poster.</summary>
    public int Kept => Volatile.Read(ref _kept);
    public int Failed => Volatile.Read(ref _failed);
    public string? LastError { get; private set; }
    public DateTime? FinishedAt { get; private set; }

    public bool TryStart()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        Total = 0; _done = 0; _switched = 0; _kept = 0; _failed = 0; LastError = null; FinishedAt = null;
        _ = Task.Run(RunAsync);
        return true;
    }

    private async Task RunAsync()
    {
        try
        {
            List<int> ids;
            await using (var db = await _factory.CreateDbContextAsync(_stopping))
                ids = await db.Movies.AsNoTracking().Where(m => m.TmdbId != null && m.LocalPosterUrl != null && m.PosterUrl == m.LocalPosterUrl)
                    .OrderBy(m => m.Id).Select(m => m.Id).ToListAsync(_stopping);
            Total = ids.Count;

            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _stopping }, async (id, ct) =>
            {
                try
                {
                    await using var db = await _factory.CreateDbContextAsync(ct);
                    var movie = await db.Movies.FirstOrDefaultAsync(m => m.Id == id, ct);
                    if (movie != null)
                    {
                        if (await _metadata.UseTmdbPosterAsync(movie, ct)) { await db.SaveChangesAsync(ct); Interlocked.Increment(ref _switched); }
                        else Interlocked.Increment(ref _kept);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    LastError = ex.Message;
                    _log.LogWarning(ex, "Poster switch for movie {Id} failed.", id);
                }
                finally { Interlocked.Increment(ref _done); }
            });
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { LastError = ex.Message; _log.LogError(ex, "Poster pass failed."); }
        finally { FinishedAt = DateTime.UtcNow; Volatile.Write(ref _running, 0); }
    }
}
