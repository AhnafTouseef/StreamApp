using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using StreamApp.Data;
using StreamApp.Infrastructure.Crawler;
using StreamApp.Infrastructure.Metadata;
using StreamApp.Models;

namespace StreamApp.Services;

/// <summary>Result of a "Rescan TMDB" run of one source. Counts are per catalog entry (movies and series together).</summary>
public sealed record RescanSnapshot(
    int SourceId, string SourceName, bool Running, DateTime StartedAt, DateTime? FinishedAt, int Total, int Processed, string? Current,
    int Matched, int NewlyMatched, int Unmatched, int NeedsReview, int Failed, int ManualKept, bool RecheckMatched, string? Error);

/// <summary>
/// Re-evaluates the TMDB associations of one source's entries - movies and series - from the data already in the database. It lists no folders and
/// runs no ffprobe: only TMDB is queried. Entries an administrator corrected by hand (<see cref="MatchStatus.Manual"/>) are never touched.
/// By default only entries that are not matched yet (unmatched or needing review) are searched again; "re-check matched" also compares the
/// current association with what the current rules would pick and flags a disagreement for review (it never silently replaces a match).
/// </summary>
public sealed class TmdbRescanService
{
    private static readonly Regex VideoExtRx = new(@"\.(mkv|mp4|avi|mov|wmv|m4v|ts|webm|flv|mpg|mpeg)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class Run
    {
        public int SourceId; public string SourceName = ""; public bool Running = true; public DateTime StartedAt = DateTime.UtcNow; public DateTime? FinishedAt;
        public int Total, Processed, Matched, NewlyMatched, Unmatched, NeedsReview, Failed, ManualKept; public string? Current; public bool Recheck; public string? Error;
        public RescanSnapshot Snapshot() => new(SourceId, SourceName, Running, StartedAt, FinishedAt, Total, Processed, Current, Matched, NewlyMatched, Unmatched, NeedsReview, Failed, ManualKept, Recheck, Error);
    }

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly MetadataService _metadata;
    private readonly ScanCoordinator _scans;
    private readonly ILogger<TmdbRescanService> _log;
    private readonly CancellationToken _stopping;
    private readonly ConcurrentDictionary<int, Run> _runs = new();

    public TmdbRescanService(IDbContextFactory<AppDbContext> factory, MetadataService metadata, ScanCoordinator scans, ILogger<TmdbRescanService> log, IHostApplicationLifetime lifetime)
    { _factory = factory; _metadata = metadata; _scans = scans; _log = log; _stopping = lifetime.ApplicationStopping; }

    public RescanSnapshot? Get(int sourceId) { lock (_runs) return _runs.TryGetValue(sourceId, out var r) ? r.Snapshot() : null; }
    public bool IsRunning(int sourceId) { lock (_runs) return _runs.TryGetValue(sourceId, out var r) && r.Running; }

    public (bool Started, string Message) TryStart(int sourceId, string sourceName, bool recheckMatched)
    {
        if (!_metadata.IsConfigured) return (false, "TMDB is not configured (set Tmdb:ApiKey).");
        if (_scans.IsRunning(sourceId)) return (false, "A scan of this source is running. Wait for it to finish.");
        Run run;
        lock (_runs)
        {
            if (_runs.TryGetValue(sourceId, out var existing) && existing.Running) return (false, "A TMDB rescan of this source is already running.");
            run = new Run { SourceId = sourceId, SourceName = sourceName, Recheck = recheckMatched };
            _runs[sourceId] = run;
        }
        _ = Task.Run(() => RunAsync(run));
        return (true, $"Rescanning TMDB for '{sourceName}'. Nothing is crawled or re-probed; progress appears on this page.");
    }

    private void Bump(Run run, Action<Run> change) { lock (_runs) change(run); }

    private async Task RunAsync(Run run)
    {
        var ct = _stopping;
        try
        {
            List<(int Id, CatalogType Type, MatchStatus Status)> rows;
            await using (var db = await _factory.CreateDbContextAsync(ct))
                rows = (await db.Movies.AsNoTracking().Where(m => m.SourceId == run.SourceId).OrderBy(m => m.Id)
                    .Select(m => new { m.Id, m.ContentType, m.MatchStatus }).ToListAsync(ct)).Select(x => (x.Id, x.ContentType, x.MatchStatus)).ToList();
            Bump(run, r => r.Total = rows.Count);

            var health = new TmdbHealth(true);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (!health.Usable)
                {
                    Bump(run, r => { r.Error = "TMDB stopped responding; the remaining entries were not checked."; r.Failed += r.Total - r.Processed; r.Processed = r.Total; });
                    break;
                }

                if (row.Status == MatchStatus.Manual) { Bump(run, r => { r.ManualKept++; r.Processed++; }); continue; }
                if (row.Status == MatchStatus.Matched && !run.Recheck) { Bump(run, r => { r.Matched++; r.Processed++; }); continue; }

                try
                {
                    var outcome = await EvaluateAsync(row.Id, row.Type, row.Status, ct);
                    health.Observe(outcome);
                    Bump(run, r =>
                    {
                        r.Processed++;
                        if (row.Status == MatchStatus.Matched)
                        {
                            if (outcome.Status == MatchStatus.Ambiguous && outcome.Note.StartsWith("Rescan suggests", StringComparison.Ordinal)) r.NeedsReview++; else r.Matched++;
                            return;
                        }
                        switch (outcome.Status)
                        {
                            case MatchStatus.Matched: r.NewlyMatched++; r.Matched++; break;
                            case MatchStatus.Ambiguous: r.NeedsReview++; break;
                            default:
                                if (outcome.Note.StartsWith("TMDB lookup failed", StringComparison.Ordinal)) r.Failed++; else r.Unmatched++;
                                break;
                        }
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "TMDB rescan of entry {Id} failed.", row.Id);
                    Bump(run, r => { r.Failed++; r.Processed++; });
                }
            }
        }
        catch (OperationCanceledException) { Bump(run, r => r.Error = "Interrupted: the application is shutting down."); }
        catch (Exception ex)
        {
            _log.LogError(ex, "TMDB rescan of source {Source} failed.", run.SourceName);
            Bump(run, r => r.Error = ex.Message);
        }
        finally { Bump(run, r => { r.Running = false; r.Current = null; r.FinishedAt = DateTime.UtcNow; }); }
    }

    private async Task<MatchOutcome> EvaluateAsync(int id, CatalogType type, MatchStatus before, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var movie = await db.Movies
            .Include(m => m.Series!).ThenInclude(s => s.Seasons).ThenInclude(s => s.Episodes)
            .Include(m => m.MovieGenres)
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (movie == null) return new MatchOutcome(MatchStatus.Unmatched, null, "Entry no longer exists.");
        if (movie.MatchStatus == MatchStatus.Manual) return new MatchOutcome(MatchStatus.Manual, movie.TmdbId, "Manual.");   // changed by an admin meanwhile

        MatchOutcome outcome;
        if (before == MatchStatus.Matched)
        {
            // re-check only: compare, never replace
            MatchOutcome found;
            try
            {
                found = type == CatalogType.Series
                    ? await _metadata.FindSeriesMatchAsync(SeriesNameParser.Parse(movie.FolderName),
                        movie.Series?.Seasons.Where(s => s.Number > 0 && !s.IsCustom).Select(s => s.Number).ToList(), ct)
                    : await FindMovieAsync(movie, ct);
            }
            catch (TmdbException ex) { return new MatchOutcome(MatchStatus.Unmatched, null, "TMDB lookup failed: " + ex.Message); }

            if (found.Status == MatchStatus.Matched && found.TmdbId != movie.TmdbId)
            {
                movie.MatchStatus = MatchStatus.Ambiguous;
                movie.MatchNote = $"Rescan suggests TMDB #{found.TmdbId} ({found.Note}) but #{movie.TmdbId} is associated. Review it.";
                await db.SaveChangesAsync(ct);
                return new MatchOutcome(MatchStatus.Ambiguous, found.TmdbId, movie.MatchNote);
            }
            return new MatchOutcome(MatchStatus.Matched, movie.TmdbId, "Confirmed.");
        }

        if (type == CatalogType.Series)
            outcome = await _metadata.MatchAndApplySeriesAsync(db, movie, SeriesNameParser.Parse(movie.FolderName), ct);
        else
        {
            var parsed = FolderNameParser.Parse(StripExt(movie.FolderName));
            outcome = await _metadata.MatchAndApplyAsync(db, movie, parsed.Title, parsed.Year ?? movie.ReleaseYear, ct);
        }
        movie.MetadataFetchedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    private Task<MatchOutcome> FindMovieAsync(Movie movie, CancellationToken ct)
    {
        var parsed = FolderNameParser.Parse(StripExt(movie.FolderName));
        return _metadata.FindMatchAsync(parsed.Title, parsed.Year ?? movie.ReleaseYear, ct);
    }

    private static string StripExt(string name) => VideoExtRx.Replace(name, "");
}
