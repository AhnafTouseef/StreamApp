using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Models;

namespace MovieHub.Services;

public sealed class MovieCopyDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? Quality { get; set; }
    public string SourceName { get; set; } = "";
    public string CategoryName { get; set; } = "";
}

/// <summary>Read-side queries for the public site. Everything is served from SQLite; nothing here touches the ISP servers.</summary>
public sealed class MovieService
{
    private readonly AppDbContext _db;

    public MovieService(AppDbContext db) => _db = db;

    private static readonly Expression<Func<Movie, MovieCardDto>> ToCard = m => new MovieCardDto
    {
        Id = m.Id,
        Title = m.Title,
        Year = m.ReleaseYear,
        Rating = m.TmdbRating,
        PosterUrl = m.PosterUrl,
        BackdropUrl = m.BackdropUrl,
        Overview = m.Overview,
        CategoryName = m.Category.Name,
        Quality = m.Quality,
        Genres = m.MovieGenres.Select(g => g.Genre.Name).ToList() // capped to 3 by MovieCardDto.Genres; Take() here would need APPLY (unsupported on SQLite)
    };

    /// <summary>Movies whose category is active. Movies of disabled sources stay visible: an offline ISP must not empty the catalog.</summary>
    private IQueryable<Movie> Visible() => _db.Movies.AsNoTracking().Where(m => m.Category.IsActive);

    // ------------------------------------------------------------------ home

    public async Task<HomeDto> GetHomeAsync(CancellationToken ct = default)
    {
        var home = new HomeDto { TotalMovies = await Visible().CountAsync(ct) };
        if (home.TotalMovies == 0) return home;

        var recentYear = DateTime.UtcNow.Year - 2;

        home.Featured = await Visible()
            .Where(m => m.BackdropUrl != null)
            .OrderByDescending(m => m.Popularity).ThenByDescending(m => m.Id)
            .Take(6).Select(ToCard).ToListAsync(ct);
        if (home.Featured.Count == 0)
            home.Featured = await Visible().Where(m => m.PosterUrl != null)
                .OrderByDescending(m => m.CreatedAt).Take(5).Select(ToCard).ToListAsync(ct);

        var trending = await Visible().Where(m => m.Popularity != null && m.ReleaseYear >= recentYear)
            .OrderByDescending(m => m.Popularity).Take(20).Select(ToCard).ToListAsync(ct);
        if (trending.Count < 5)
            trending = await Visible().Where(m => m.Popularity != null)
                .OrderByDescending(m => m.Popularity).Take(20).Select(ToCard).ToListAsync(ct);

        var recent = await Visible().OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(20).Select(ToCard).ToListAsync(ct);
        var popular = await Visible().Where(m => m.TmdbVoteCount != null)
            .OrderByDescending(m => m.TmdbVoteCount).Take(20).Select(ToCard).ToListAsync(ct);
        var top = await Visible().Where(m => m.TmdbRating != null && m.TmdbVoteCount >= 100)
            .OrderByDescending(m => m.TmdbRating).Take(20).Select(ToCard).ToListAsync(ct);
        var recommended = await Visible().Where(m => m.TmdbRating >= 7.0)
            .OrderBy(m => EF.Functions.Random()).Take(20).Select(ToCard).ToListAsync(ct);

        void AddRow(string id, string title, List<MovieCardDto> items)
        {
            if (items.Count > 0) home.Rows.Add(new MovieRowModel { Id = id, Title = title, Items = items });
        }
        AddRow("trending", "Trending Movies", trending);
        AddRow("recent", "Recently Added", recent);
        AddRow("popular", "Popular Movies", popular);
        AddRow("top", "Top Rated", top);
        AddRow("recommended", "Recommended Movies", recommended);

        home.Genres = (await GetGenresAsync(ct)).Take(24).ToList();
        return home;
    }

    // ------------------------------------------------------------------ browsing / search

    public async Task<PagedResult<MovieCardDto>> BrowseAsync(BrowseRequest r, CancellationToken ct = default)
    {
        var q = Visible();

        if (r.CategoryIds is { Count: > 0 })
        {
            var ids = r.CategoryIds.ToList();
            q = q.Where(m => ids.Contains(m.CategoryId));
        }

        if (!string.IsNullOrWhiteSpace(r.GenreSlug))
        {
            var slug = r.GenreSlug.Trim();
            q = q.Where(m => m.MovieGenres.Any(mg => mg.Genre.Slug == slug));
        }

        if (r.Year is { } filterYear)
            q = q.Where(m => m.ReleaseYear == filterYear);

        if (!string.IsNullOrWhiteSpace(r.Query))
        {
            var text = r.Query.Trim();
            var like = "%" + EscapeLike(text) + "%";
            if (int.TryParse(text, out var yearInText) && yearInText is >= 1880 and <= 2100)
            {
                q = q.Where(m => m.ReleaseYear == yearInText
                    || EF.Functions.Like(m.Title, like, "\\")
                    || (m.OriginalTitle != null && EF.Functions.Like(m.OriginalTitle, like, "\\")));
            }
            else
            {
                q = q.Where(m => EF.Functions.Like(m.Title, like, "\\")
                    || (m.OriginalTitle != null && EF.Functions.Like(m.OriginalTitle, like, "\\"))
                    || m.MovieGenres.Any(mg => EF.Functions.Like(mg.Genre.Name, like, "\\")));
            }
        }

        IOrderedQueryable<Movie> ordered = (r.Sort ?? "recent").ToLowerInvariant() switch
        {
            "year" => q.OrderByDescending(m => m.ReleaseYear).ThenBy(m => EF.Functions.Collate(m.Title, "NOCASE")),
            "rating" => q.OrderByDescending(m => m.TmdbRating).ThenBy(m => EF.Functions.Collate(m.Title, "NOCASE")),
            "alpha" => q.OrderBy(m => EF.Functions.Collate(m.Title, "NOCASE")).ThenBy(m => m.ReleaseYear),
            _ => q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
        };

        var pageSize = Math.Clamp(r.PageSize, 6, 120);
        var total = await q.CountAsync(ct);
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var page = Math.Clamp(r.Page, 1, pages);

        var items = await ordered.ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(ToCard).ToListAsync(ct);

        return new PagedResult<MovieCardDto> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<FacetsDto> GetFacetsAsync(IReadOnlyCollection<int>? categoryIds, CancellationToken ct = default)
    {
        var q = Visible();
        if (categoryIds is { Count: > 0 })
        {
            var ids = categoryIds.ToList();
            q = q.Where(m => ids.Contains(m.CategoryId));
        }

        var years = await q.Where(m => m.ReleaseYear != null).Select(m => m.ReleaseYear).Distinct().ToListAsync(ct);

        var genreRows = await q.SelectMany(m => m.MovieGenres)
            .GroupBy(mg => new { mg.Genre.Id, mg.Genre.Name, mg.Genre.Slug })
            .Select(g => new { g.Key.Id, g.Key.Name, g.Key.Slug, Count = g.Count() })
            .ToListAsync(ct);

        return new FacetsDto
        {
            Years = years.Where(y => y.HasValue).Select(y => y!.Value).OrderByDescending(y => y).ToList(),
            Genres = genreRows.OrderBy(g => g.Name)
                .Select(g => new GenreFacet { Id = g.Id, Name = g.Name, Slug = g.Slug, Count = g.Count }).ToList()
        };
    }

    // ------------------------------------------------------------------ genres

    public async Task<List<GenreFacet>> GetGenresAsync(CancellationToken ct = default)
    {
        var rows = await _db.Genres.AsNoTracking()
            .Select(g => new GenreFacet
            {
                Id = g.Id, Name = g.Name, Slug = g.Slug,
                Count = g.MovieGenres.Count(mg => mg.Movie.Category.IsActive)
            }).ToListAsync(ct);
        return rows.Where(g => g.Count > 0).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<Genre?> GetGenreBySlugAsync(string slug, CancellationToken ct = default) =>
        _db.Genres.AsNoTracking().FirstOrDefaultAsync(g => g.Slug == slug, ct);

    // ------------------------------------------------------------------ details / watch

    public Task<Movie?> GetMovieAsync(int id, CancellationToken ct = default) =>
        _db.Movies.AsNoTracking()
            .Include(m => m.Category)
            .Include(m => m.Source)
            .Include(m => m.MovieGenres).ThenInclude(mg => mg.Genre)
            .Include(m => m.MediaFiles).ThenInclude(f => f.Streams)
            .Include(m => m.Subtitles)
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<List<MovieCardDto>> GetSimilarAsync(Movie movie, int take = 18, CancellationToken ct = default)
    {
        var genreIds = movie.MovieGenres.Select(g => g.GenreId).ToList();
        if (genreIds.Count == 0) return new List<MovieCardDto>();
        var movieId = movie.Id;
        var tmdbId = movie.TmdbId;

        return await Visible()
            .Where(m => m.Id != movieId && (tmdbId == null || m.TmdbId != tmdbId))
            .Where(m => m.MovieGenres.Any(mg => genreIds.Contains(mg.GenreId)))
            .OrderByDescending(m => m.MovieGenres.Count(mg => genreIds.Contains(mg.GenreId)))
            .ThenByDescending(m => m.TmdbRating)
            .Take(take).Select(ToCard).ToListAsync(ct);
    }

    /// <summary>Other catalog entries with the same TMDB id — separate copies the viewer can choose between.</summary>
    public async Task<List<MovieCopyDto>> GetOtherCopiesAsync(Movie movie, CancellationToken ct = default)
    {
        if (movie.TmdbId is not { } tmdbId) return new List<MovieCopyDto>();
        var movieId = movie.Id;
        return await Visible().Where(m => m.TmdbId == tmdbId && m.Id != movieId)
            .OrderBy(m => m.Id)
            .Select(m => new MovieCopyDto
            {
                Id = m.Id, Title = m.Title, Year = m.ReleaseYear, Quality = m.Quality,
                SourceName = m.Source.Name, CategoryName = m.Category.Name
            }).ToListAsync(ct);
    }
}
