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
    private readonly SearchIndex _search;

    public MovieService(AppDbContext db, SearchIndex search) { _db = db; _search = search; }

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

    /// <summary>
    /// Movies whose category is shown: the category and all of its ancestors (up to 4 levels) are active, so hiding a category hides its movies
    /// and those of its sub-categories. Movies of disabled sources stay visible: an offline ISP must not empty the catalog.
    /// </summary>
    private IQueryable<Movie> Visible() => _db.Movies.AsNoTracking().Where(VisibleMovie);

    public static readonly System.Linq.Expressions.Expression<Func<Movie, bool>> VisibleMovie = m =>
        m.Category.IsActive
        && (m.Category.Parent == null || (m.Category.Parent.IsActive
            && (m.Category.Parent.Parent == null || (m.Category.Parent.Parent.IsActive
                && (m.Category.Parent.Parent.Parent == null || m.Category.Parent.Parent.Parent!.IsActive)))));

    /// <summary>Whether the movie is shown on the site (its category chain is active). Used to refuse direct links to hidden movies.</summary>
    public Task<bool> IsVisibleAsync(int movieId, CancellationToken ct = default) =>
        _db.Movies.AsNoTracking().Where(m => m.Id == movieId).Where(VisibleMovie).AnyAsync(ct);

    // ------------------------------------------------------------------ home

    public async Task<HomeDto> GetHomeAsync(CancellationToken ct = default)
    {
        var home = new HomeDto { TotalMovies = await Visible().CountAsync(ct) };
        if (home.TotalMovies == 0) return home;

        var recentYear = DateTime.UtcNow.Year - 2;
        var hasPopularity = await Visible().AnyAsync(m => m.Popularity != null, ct);

        home.Featured = await Visible()
            .Where(m => m.BackdropUrl != null)
            .OrderByDescending(m => m.Popularity).ThenByDescending(m => m.Id)
            .Take(6).Select(ToCard).ToListAsync(ct);
        if (home.Featured.Count == 0)
            home.Featured = await Visible().Where(m => m.PosterUrl != null)
                .OrderByDescending(m => m.Popularity).ThenByDescending(m => m.ReleaseYear).ThenByDescending(m => m.Id)
                .Take(5).Select(ToCard).ToListAsync(ct);

        void AddRow(string id, string title, List<MovieCardDto> items)
        {
            if (items.Count > 0) home.Rows.Add(new MovieRowModel { Id = id, Title = title, Items = items });
        }

        if (hasPopularity)
        {
            // Trending = what is popular right now among recent releases; if too few recent ones have data, widen to all years.
            var trending = await Visible().Where(m => m.Popularity != null && m.ReleaseYear >= recentYear)
                .OrderByDescending(m => m.Popularity).Take(20).Select(ToCard).ToListAsync(ct);
            if (trending.Count < 5)
                trending = await Visible().Where(m => m.Popularity != null)
                    .OrderByDescending(m => m.Popularity).Take(20).Select(ToCard).ToListAsync(ct);
            var popular = await Visible().Where(m => m.TmdbVoteCount != null)
                .OrderByDescending(m => m.TmdbVoteCount).Take(20).Select(ToCard).ToListAsync(ct);
            var top = await Visible().Where(m => m.TmdbRating != null && m.TmdbVoteCount >= 100)
                .OrderByDescending(m => m.TmdbRating).Take(20).Select(ToCard).ToListAsync(ct);
            var recommended = await Visible().Where(m => m.TmdbRating >= 7.0)
                .OrderBy(m => EF.Functions.Random()).Take(20).Select(ToCard).ToListAsync(ct);

            AddRow("trending", "Trending Now", trending);
            AddRow("popular", "Popular Movies", popular);
            AddRow("top", "Top Rated", top);
            AddRow("recommended", "Recommended for You", recommended);
        }
        else
        {
            // No TMDB data yet (no API key, or matching has not run): popularity is unknown, so show the newest releases instead of an empty page.
            var fresh = await Visible().OrderByDescending(m => m.ReleaseYear).ThenByDescending(m => m.Id)
                .Take(20).Select(ToCard).ToListAsync(ct);
            AddRow("new-releases", "New Releases", fresh);
        }

        home.CategoryRows = await GetCategorySlidersAsync(recentYear, ct);

        home.Genres = (await GetGenresAsync(ct)).Take(24).ToList();
        return home;
    }

    /// <summary>
    /// One slider per active top-level category (sub-categories included): the most popular movies of the last few years,
    /// topped up with older popular ones when a category has few recent titles. Without popularity data the newest releases are shown.
    /// </summary>
    private async Task<List<MovieRowModel>> GetCategorySlidersAsync(int recentYear, CancellationToken ct)
    {
        var rows = new List<MovieRowModel>();
        var categories = CategoryService.EffectivelyActive(await _db.Categories.AsNoTracking().OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToListAsync(ct));
        var byParent = categories.Where(c => c.ParentCategoryId != null).GroupBy(c => c.ParentCategoryId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var top in categories.Where(c => c.ParentCategoryId == null))
        {
            var ids = new List<int> { top.Id };
            for (var i = 0; i < ids.Count; i++)
                if (byParent.TryGetValue(ids[i], out var kids)) ids.AddRange(kids.Select(k => k.Id).Where(k => !ids.Contains(k)));

            var scope = Visible().Where(m => ids.Contains(m.CategoryId));
            var popularityKnown = await scope.AnyAsync(m => m.Popularity != null, ct);

            var recent = await scope.Where(m => m.ReleaseYear >= recentYear - 1)
                .OrderByDescending(m => m.Popularity).ThenByDescending(m => m.ReleaseYear).ThenByDescending(m => m.Id)
                .Take(20).Select(ToCard).ToListAsync(ct);

            if (recent.Count < 10)
            {
                var have = recent.Select(r => r.Id).ToList();
                var more = await scope.Where(m => !have.Contains(m.Id))
                    .OrderByDescending(m => m.Popularity).ThenByDescending(m => m.ReleaseYear).ThenByDescending(m => m.Id)
                    .Take(20 - recent.Count).Select(ToCard).ToListAsync(ct);
                recent.AddRange(more);
            }
            if (recent.Count == 0) continue;

            rows.Add(new MovieRowModel
            {
                Id = "cat-" + top.Slug,
                Title = popularityKnown ? $"Popular in {top.Name}" : $"New in {top.Name}",
                Items = recent,
                LinkUrl = $"/movies/{top.Slug}" + (popularityKnown ? "?sort=popular" : "?sort=year")
            });
        }
        return rows;
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

        var filtered = q;   // category / genre / year filters applied, text search not yet
        var fuzzyText = (string?)null;
        if (!string.IsNullOrWhiteSpace(r.Query))
        {
            var text = r.Query.Trim();
            var like = "%" + EscapeLike(text) + "%";
            var isYear = int.TryParse(text, out var yearInText) && yearInText is >= 1880 and <= 2100;
            if (isYear)
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
                // nothing contains the text as typed: fall back to similar titles (typos, punctuation, word order)
                if (!await q.AnyAsync(ct)) fuzzyText = text;
            }
        }

        if (fuzzyText != null)
        {
            var fuzzy = await BrowseFuzzyAsync(filtered, fuzzyText, r, ct);
            if (fuzzy != null) return fuzzy;
        }

        IOrderedQueryable<Movie> ordered = (r.Sort ?? "popular").ToLowerInvariant() switch
        {
            "recent" => q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id),
            "year" => q.OrderByDescending(m => m.ReleaseYear).ThenBy(m => EF.Functions.Collate(m.Title, "NOCASE")),
            "rating" => q.OrderByDescending(m => m.TmdbRating).ThenBy(m => EF.Functions.Collate(m.Title, "NOCASE")),
            "alpha" => q.OrderBy(m => EF.Functions.Collate(m.Title, "NOCASE")).ThenBy(m => m.ReleaseYear),
            _ => q.OrderByDescending(m => m.Popularity).ThenByDescending(m => m.ReleaseYear).ThenBy(m => EF.Functions.Collate(m.Title, "NOCASE"))   // "popular" is the default
        };

        var pageSize = Math.Clamp(r.PageSize, 6, 120);
        var total = await q.CountAsync(ct);
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var page = Math.Clamp(r.Page, 1, pages);

        var items = await ordered.ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(ToCard).ToListAsync(ct);

        return new PagedResult<MovieCardDto> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    /// <summary>One row of the search box's drop-down.</summary>
    public sealed class SuggestionDto
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public int? Year { get; set; }
        public string? PosterUrl { get; set; }
        public string? CategoryName { get; set; }
    }

    /// <summary>Titles matching the letters typed so far, for the header search box. Hidden categories are excluded like everywhere else.</summary>
    public async Task<IReadOnlyList<SuggestionDto>> SuggestAsync(string typed, int max, CancellationToken ct)
    {
        var hits = await _search.SuggestAsync(typed, Math.Max(max * 3, 12), ct);   // a few extra: some may sit in hidden categories
        if (hits.Count == 0) return Array.Empty<SuggestionDto>();
        var ids = hits.Select(h => h.Id).ToList();
        var rows = await Visible().Where(m => ids.Contains(m.Id))
            .Select(m => new SuggestionDto { Id = m.Id, Title = m.Title, Year = m.ReleaseYear, PosterUrl = m.PosterUrl, CategoryName = m.Category.Name }).ToListAsync(ct);
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return rows.OrderBy(r => order[r.Id]).Take(max).ToList();
    }

    /// <summary>Similar-title results, best match first. Null when nothing is similar enough (the caller then shows the normal empty result).</summary>
    private async Task<PagedResult<MovieCardDto>?> BrowseFuzzyAsync(IQueryable<Movie> filtered, string text, BrowseRequest r, CancellationToken ct)
    {
        var hits = await _search.SearchAsync(text, 100, ct);
        if (hits.Count == 0) return null;

        var candidateIds = hits.Select(h => h.Id).ToList();
        var allowed = (await filtered.Where(m => candidateIds.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct)).ToHashSet();   // honours hidden categories and the active filters
        var ranked = hits.Where(h => allowed.Contains(h.Id)).ToList();
        if (ranked.Count == 0) return null;

        var pageSize = Math.Clamp(r.PageSize, 6, 120);
        var pages = Math.Max(1, (int)Math.Ceiling(ranked.Count / (double)pageSize));
        var page = Math.Clamp(r.Page, 1, pages);
        var slice = ranked.Skip((page - 1) * pageSize).Take(pageSize).Select(h => h.Id).ToList();
        var cards = await _db.Movies.AsNoTracking().Where(m => slice.Contains(m.Id)).Select(ToCard).ToListAsync(ct);
        var order = slice.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        cards = cards.OrderBy(c => order[c.Id]).ToList();

        var top = ranked[0];
        var notice = string.Equals(top.Title, text, StringComparison.OrdinalIgnoreCase)
            ? $"No exact match for “{text}”. Showing the closest titles."
            : $"No exact match for “{text}”. Did you mean “{top.Title}”? Showing the closest titles.";
        return new PagedResult<MovieCardDto> { Items = cards, Page = page, PageSize = pageSize, TotalCount = ranked.Count, Notice = notice };
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
                Count = g.MovieGenres.Count(mg => mg.Movie.Category.IsActive
                    && (mg.Movie.Category.Parent == null || (mg.Movie.Category.Parent.IsActive
                        && (mg.Movie.Category.Parent.Parent == null || (mg.Movie.Category.Parent.Parent.IsActive
                            && (mg.Movie.Category.Parent.Parent.Parent == null || mg.Movie.Category.Parent.Parent.Parent!.IsActive))))))
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
