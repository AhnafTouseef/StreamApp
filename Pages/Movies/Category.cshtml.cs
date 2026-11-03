using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Movies;

public class CategoryModel : PageModel
{
    private readonly CategoryService _categories;
    private readonly MovieService _movies;
    private readonly ScanCoordinator _scans;
    private readonly AppDbContext _db;

    public CategoryModel(CategoryService categories, MovieService movies, ScanCoordinator scans, AppDbContext db)
    {
        _categories = categories; _movies = movies; _scans = scans; _db = db;
    }

    public ResolvedCategory Resolved { get; private set; } = null!;
    public BrowseViewModel Browse { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(string slug, string? q, string? genre, int? year, string? sort, int page = 1)
    {
        var ct = HttpContext.RequestAborted;
        var resolved = await _categories.ResolveAsync(slug, ct);
        if (resolved == null) return NotFound();
        Resolved = resolved;

        // Never blocks: the check (and any scan) runs in the background while this page is served from SQLite.
        _scans.NotifyCategoryVisit(resolved.AllIds);

        var result = await _movies.BrowseAsync(new BrowseRequest
        {
            CategoryIds = resolved.AllIds, Query = q, GenreSlug = genre, Year = year, Sort = sort ?? "recent", Page = page
        }, ct);

        var sourceIds = await _db.Sources.AsNoTracking().Where(s => resolved.AllIds.Contains(s.CategoryId)).Select(s => s.Id).ToListAsync(ct);

        // Subcategory switcher: on a child page show its siblings, on a parent page show its children.
        var tabs = new List<BrowseTab>();
        var parent = resolved.Parent ?? (resolved.Children.Count > 0 ? resolved.Category : null);
        if (parent != null)
        {
            var siblings = resolved.Parent != null
                ? (await _categories.ResolveAsync(parent.Slug, ct))?.Children ?? new List<Category>()
                : resolved.Children;
            tabs.Add(new BrowseTab("All", $"/movies/{parent.Slug}", resolved.Parent == null));
            tabs.AddRange(siblings.Select(c => new BrowseTab(c.Name, $"/movies/{c.Slug}", c.Id == resolved.Category.Id)));
        }

        Browse = new BrowseViewModel
        {
            BasePath = $"/movies/{resolved.Category.Slug}",
            Result = result,
            Facets = await _movies.GetFacetsAsync(resolved.AllIds, ct),
            Query = q, Genre = genre, Year = year, Sort = sort ?? "recent",
            QueryPlaceholder = $"Search {resolved.Category.Name}",
            Tabs = tabs,
            ScanActive = sourceIds.Any(id => _scans.IsRunning(id)),
            EmptyTitle = "Nothing here yet",
            EmptyMessage = q != null || genre != null || year != null
                ? "No movies match these filters."
                : "This category has no movies yet. If a source is configured for it, a scan has been started in the background; reload in a moment."
        };
        return Page();
    }
}
