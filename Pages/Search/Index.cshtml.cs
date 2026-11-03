using Microsoft.AspNetCore.Mvc.RazorPages;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Search;

public class IndexModel : PageModel
{
    private readonly MovieService _movies;
    private readonly CategoryService _categories;
    public IndexModel(MovieService movies, CategoryService categories) { _movies = movies; _categories = categories; }

    public BrowseViewModel Browse { get; private set; } = new();
    public List<NavCategory> CategoryOptions { get; private set; } = new();
    public string? CategorySlug { get; private set; }

    public async Task OnGetAsync(string? q, string? category, string? genre, int? year, string? sort, int page = 1)
    {
        var ct = HttpContext.RequestAborted;
        CategoryOptions = (await _categories.GetNavigationAsync(ct)).SelectMany(c => new[] { c }.Concat(c.Children)).ToList();

        IReadOnlyCollection<int>? ids = null;
        if (!string.IsNullOrEmpty(category) && await _categories.ResolveAsync(category, ct) is { } resolved)
        {
            ids = resolved.AllIds;
            CategorySlug = resolved.Category.Slug;
        }

        var active = !string.IsNullOrWhiteSpace(q) || ids != null || genre != null || year != null;
        Browse = new BrowseViewModel
        {
            BasePath = "/search",
            Result = active
                ? await _movies.BrowseAsync(new BrowseRequest { Query = q, CategoryIds = ids, GenreSlug = genre, Year = year, Sort = sort ?? "recent", Page = page }, ct)
                : new PagedResult<MovieCardDto>(),
            Facets = await _movies.GetFacetsAsync(ids, ct),
            Query = q, Genre = genre, Year = year, Sort = sort ?? "recent",
            ShowQuery = false,
            EmptyTitle = "No results", EmptyMessage = "Nothing matched. Check the spelling or try a shorter title."
        };
    }
}
