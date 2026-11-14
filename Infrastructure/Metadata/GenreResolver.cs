using Microsoft.EntityFrameworkCore;
using StreamApp.Data;
using StreamApp.Models;
using StreamApp.Services;

namespace StreamApp.Infrastructure.Metadata;

/// <summary>
/// Creates genre rows on demand. Runs in its own short transaction behind a lock so concurrent folder tasks
/// never race to insert the same TMDB genre; callers then link movies by genre id.
/// </summary>
public sealed class GenreResolver
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GenreResolver(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<int>> EnsureAsync(IEnumerable<TmdbGenre> genres, CancellationToken ct)
    {
        var wanted = genres.Where(g => !string.IsNullOrWhiteSpace(g.Name)).GroupBy(g => g.Id).Select(g => g.First()).ToList();
        if (wanted.Count == 0) return new List<int>();

        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            var tmdbIds = wanted.Select(g => g.Id).ToList();
            var existing = await db.Genres.Where(g => g.TmdbGenreId != null && tmdbIds.Contains(g.TmdbGenreId.Value)).ToListAsync(ct);

            foreach (var g in wanted)
            {
                if (existing.Any(e => e.TmdbGenreId == g.Id)) continue;
                var slug = Slugger.Make(g.Name);
                if (await db.Genres.AnyAsync(x => x.Slug == slug, ct)) slug = $"{slug}-{g.Id}";
                var created = new Genre { Name = g.Name, Slug = slug, TmdbGenreId = g.Id };
                db.Genres.Add(created);
                existing.Add(created);
            }
            await db.SaveChangesAsync(ct);
            return tmdbIds.Select(id => existing.First(e => e.TmdbGenreId == id).Id).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }
}
