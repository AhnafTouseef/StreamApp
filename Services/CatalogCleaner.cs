using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Models;

namespace MovieHub.Services;

/// <summary>
/// Removes catalog entries in batches. Only MovieHub's own database rows are touched; the ISP server is never contacted.
/// Files, streams, subtitles and genre links of a movie are removed by the database cascade.
/// </summary>
public static class CatalogCleaner
{
    private const int Batch = 200;

    /// <summary>Deletes every movie the query matches. Returns how many were removed.</summary>
    public static async Task<int> DeleteMoviesAsync(AppDbContext db, IQueryable<Movie> matching, CancellationToken ct)
    {
        var removed = 0;
        // Snapshot the ids first: the query must not be re-evaluated against a table that is shrinking under it.
        var ids = await matching.AsNoTracking().Select(m => m.Id).ToListAsync(ct);
        for (var i = 0; i < ids.Count; i += Batch)
        {
            var chunk = ids.Skip(i).Take(Batch).ToList();
            var movies = await db.Movies.Where(m => chunk.Contains(m.Id)).ToListAsync(ct);
            db.Movies.RemoveRange(movies);
            await db.SaveChangesAsync(ct);
            removed += movies.Count;
            db.ChangeTracker.Clear();
        }
        return removed;
    }

    /// <summary>
    /// Removes all movies of a source and forgets what the crawler knew about it (scanned folders, last-scan state),
    /// so the next scan starts from scratch. The source itself stays.
    /// </summary>
    public static async Task<int> ClearSourceAsync(AppDbContext db, int sourceId, CancellationToken ct)
    {
        var removed = await DeleteMoviesAsync(db, db.Movies.Where(m => m.SourceId == sourceId), ct);

        var dirs = await db.SourceDirectories.Where(d => d.SourceId == sourceId).ToListAsync(ct);
        db.SourceDirectories.RemoveRange(dirs);

        var s = await db.Sources.FirstOrDefaultAsync(x => x.Id == sourceId, ct);
        if (s != null)
        {
            s.LastSuccessfulScan = null;
            s.LastScanAttempt = null;
            s.LastScanStatus = null;
            s.LastError = null;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return removed;
    }

    /// <summary>Removes a source together with all of its movies. Returns the number of movies removed.</summary>
    public static async Task<int> DeleteSourceAsync(AppDbContext db, int sourceId, CancellationToken ct)
    {
        var removed = await DeleteMoviesAsync(db, db.Movies.Where(m => m.SourceId == sourceId), ct);
        var s = await db.Sources.FirstOrDefaultAsync(x => x.Id == sourceId, ct);
        if (s != null)
        {
            db.Sources.Remove(s); // scan history and scanned-folder rows cascade
            await db.SaveChangesAsync(ct);
        }
        db.ChangeTracker.Clear();
        return removed;
    }
}
