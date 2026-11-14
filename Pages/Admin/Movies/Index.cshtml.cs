using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using StreamApp.Data;
using StreamApp.Models;
using StreamApp.Services;

namespace StreamApp.Pages.Admin.Movies;

public sealed record AdminMovieRow(int Id, string Title, int? Year, string FolderName, string CategoryName, string SourceName, int? TmdbId, MatchStatus Match, int Files);

public class IndexModel : AdminPageModel
{
    private const int PageSize = 40;
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public string? Q { get; private set; }
    public int? Source { get; private set; }
    public int? Category { get; private set; }
    /// <summary>Category options in tree order with their depth, for the filter dropdown.</summary>
    public List<(Category Category, int Depth)> CategoryOptions { get; private set; } = new();
    public bool HasFilter => !string.IsNullOrWhiteSpace(Q) || Source.HasValue || Category.HasValue || Match.HasValue;
    public MatchStatus? Match { get; private set; }
    public int Total { get; private set; }
    public int PageNo { get; private set; } = 1;
    public int Pages { get; private set; } = 1;
    public List<AdminMovieRow> Items { get; private set; } = new();
    public List<Source> Sources { get; private set; } = new();
    public Dictionary<string, string?> QueryMap => new() { ["q"] = Q, ["source"] = Source?.ToString(), ["category"] = Category?.ToString(), ["match"] = Match?.ToString() };

    public async Task OnGetAsync(string? q, int? source, int? category, MatchStatus? match, [FromQuery(Name = "page")] int page = 1)  // explicit: Razor Pages reserves the route value "page", so an unannotated parameter never sees ?page=N
    {
        var ct = HttpContext.RequestAborted;
        Q = q; Source = source; Category = category; Match = match;
        Sources = await _db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var allCategories = await _db.Categories.AsNoTracking().OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToListAsync(ct);
        CategoryOptions = Tree(allCategories);

        var query = Filtered(_db.Movies.AsNoTracking(), q, source, CategoryScope(allCategories, category), match);

        Total = await query.CountAsync(ct);
        Pages = Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
        PageNo = Math.Clamp(page, 1, Pages);
        Items = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Skip((PageNo - 1) * PageSize).Take(PageSize)
            .Select(m => new AdminMovieRow(m.Id, m.Title, m.ReleaseYear, m.FolderName, m.Category.Name, m.Source.Name, m.TmdbId, m.MatchStatus, m.MediaFiles.Count))
            .ToListAsync(ct);
    }

    private static List<(Category, int)> Tree(List<Category> all)
    {
        var result = new List<(Category, int)>(); var seen = new HashSet<int>(); var ids = all.Select(c => c.Id).ToHashSet();
        void Walk(int? parent, int depth)
        {
            foreach (var c in all.Where(x => x.ParentCategoryId == parent || (parent == null && x.ParentCategoryId is { } p && !ids.Contains(p))))
            {
                if (!seen.Add(c.Id)) continue;
                result.Add((c, depth)); Walk(c.Id, depth + 1);
            }
        }
        Walk(null, 0);
        return result;
    }

    /// <summary>The chosen category plus all of its sub-categories (a filter on "English" includes "English 720").</summary>
    private static List<int>? CategoryScope(List<Category> all, int? category)
    {
        if (!category.HasValue) return null;
        var ids = new List<int> { category.Value };
        for (var i = 0; i < ids.Count; i++) ids.AddRange(all.Where(c => c.ParentCategoryId == ids[i]).Select(c => c.Id).Where(id => !ids.Contains(id)));
        return ids;
    }

    private static IQueryable<Movie> Filtered(IQueryable<Movie> query, string? q, int? source, List<int>? categoryIds, MatchStatus? match)
    {
        if (categoryIds != null) query = query.Where(m => categoryIds.Contains(m.CategoryId));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(m => EF.Functions.Like(m.Title, like, "\\") || EF.Functions.Like(m.FolderName, like, "\\"));
        }
        if (source.HasValue) query = query.Where(m => m.SourceId == source.Value);
        if (match.HasValue) query = query.Where(m => m.MatchStatus == match.Value);
        return query;
    }

    public async Task<IActionResult> OnPostDeleteSelectedAsync(int[] ids, string? q, int? source, int? category, MatchStatus? match, int pageNo = 1)
    {
        if (ids == null || ids.Length == 0) { Fail("Tick at least one entry first."); return Back(q, source, category, match, pageNo); }
        var distinct = ids.Distinct().Take(5000).ToArray();
        var removed = await CatalogCleaner.DeleteMoviesAsync(_db, _db.Movies.Where(m => distinct.Contains(m.Id)), HttpContext.RequestAborted);
        Ok($"Removed {removed:N0} entr{(removed == 1 ? "y" : "ies")} from the catalog. Nothing was deleted on the ISP server. A deep scan can rediscover them.");
        return Back(q, source, category, match, pageNo);
    }

    /// <summary>Deletes everything the current filter matches (all pages). Requires at least one filter so "delete all" is never a single stray click.</summary>
    public async Task<IActionResult> OnPostDeleteMatchingAsync(string? q, int? source, int? category, MatchStatus? match)
    {
        if (string.IsNullOrWhiteSpace(q) && !source.HasValue && !category.HasValue && !match.HasValue)
        {
            Fail("Choose a source, a category, a match status or a search text first, so you do not wipe the whole catalog by accident.");
            return Back(q, source, category, match, 1);
        }
        var allCategories = await _db.Categories.AsNoTracking().ToListAsync(HttpContext.RequestAborted);
        var removed = await CatalogCleaner.DeleteMoviesAsync(_db, Filtered(_db.Movies, q, source, CategoryScope(allCategories, category), match), HttpContext.RequestAborted);
        Ok($"Removed {removed:N0} entr{(removed == 1 ? "y" : "ies")} matching the filter. Nothing was deleted on the ISP server.");
        return Back(q, source, category, match, 1);
    }

    // Built by hand: a route value named "page" is the Razor page name in RedirectToPage, so it cannot carry the page number.
    private IActionResult Back(string? q, int? source, int? category, MatchStatus? match, int pageNo)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(q)) parts.Add("q=" + Uri.EscapeDataString(q));
        if (source.HasValue) parts.Add("source=" + source.Value);
        if (category.HasValue) parts.Add("category=" + category.Value);
        if (match.HasValue) parts.Add("match=" + match.Value);
        if (pageNo > 1) parts.Add("page=" + pageNo);
        return LocalRedirect("/admin/movies" + (parts.Count > 0 ? "?" + string.Join("&", parts) : ""));
    }
}
