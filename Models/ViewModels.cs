namespace MovieHub.Models;

public sealed class MovieCardDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public double? Rating { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? Overview { get; set; }
    public string? CategoryName { get; set; }
    public string? Quality { get; set; }
    private List<string> _genres = new();
    /// <summary>
    /// At most three genre names for the card chips. The cap is applied here, after the rows are materialised:
    /// a .Take(3) inside the EF projection becomes a correlated sub-select (APPLY), which SQLite cannot execute.
    /// </summary>
    public List<string> Genres
    {
        get => _genres;
        set => _genres = value is { Count: > 3 } ? value.GetRange(0, 3) : value;
    }
}

public sealed class MovieRowModel
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public IReadOnlyList<MovieCardDto> Items { get; set; } = Array.Empty<MovieCardDto>();
    /// <summary>Optional "see all" link shown in the row header.</summary>
    public string? LinkUrl { get; set; }
    public string? Subtitle { get; set; }
}

public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 36;
    public int TotalCount { get; set; }
    /// <summary>A line shown above the results, e.g. that they are similar titles rather than exact matches.</summary>
    public string? Notice { get; set; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed class BrowseRequest
{
    public IReadOnlyCollection<int>? CategoryIds { get; set; }
    public string? Query { get; set; }
    public string? GenreSlug { get; set; }
    public int? Year { get; set; }
    public string Sort { get; set; } = "popular";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 36;
}

public sealed class GenreFacet
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public int Count { get; set; }
}

public sealed class FacetsDto
{
    public List<GenreFacet> Genres { get; set; } = new();
    public List<int> Years { get; set; } = new();
}

public sealed class HomeDto
{
    public List<MovieCardDto> Featured { get; set; } = new();
    public List<MovieRowModel> Rows { get; set; } = new();
    /// <summary>One slider per top-level category with its recent popular movies.</summary>
    public List<MovieRowModel> CategoryRows { get; set; } = new();
    public List<GenreFacet> Genres { get; set; } = new();
    public int TotalMovies { get; set; }
}

public sealed class NavCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public List<NavCategory> Children { get; set; } = new();
}

public sealed class ResolvedCategory
{
    public Category Category { get; set; } = null!;
    public Category? Parent { get; set; }
    public List<Category> Children { get; set; } = new();
    /// <summary>The category itself plus all descendants — the ids whose movies belong on the page.</summary>
    public List<int> AllIds { get; set; } = new();
}

public sealed record BrowseTab(string Name, string Url, bool Active);

/// <summary>Everything the shared poster-grid partial (_Browse) needs; used by category, genre and search pages.</summary>
public sealed class BrowseViewModel
{
    public string BasePath { get; set; } = "";
    public PagedResult<MovieCardDto> Result { get; set; } = new();
    public FacetsDto Facets { get; set; } = new();
    public string? Query { get; set; }
    public string? Genre { get; set; }
    public int? Year { get; set; }
    public string Sort { get; set; } = "popular";
    public bool ShowQuery { get; set; } = true;
    public string QueryPlaceholder { get; set; } = "Search in this list";
    public List<BrowseTab> Tabs { get; set; } = new();
    public bool ScanActive { get; set; }
    public string EmptyTitle { get; set; } = "No movies found";
    public string EmptyMessage { get; set; } = "Try changing the filters.";
}
