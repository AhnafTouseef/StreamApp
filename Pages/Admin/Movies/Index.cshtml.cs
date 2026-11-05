using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Admin.Movies;

public sealed record AdminMovieRow(int Id, string Title, int? Year, string FolderName, string SourceName, int? TmdbId, MatchStatus Match, int Files);

public class IndexModel : AdminPageModel
{
    private const int PageSize = 40;
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public string? Q { get; private set; }
    public int? Source { get; private set; }
    public bool HasFilter => !string.IsNullOrWhiteSpace(Q) || Source.HasValue || Match.HasValue;
    public MatchStatus? Match { get; private set; }
    public int Total { get; private set; }
    public int PageNo { get; private set; } = 1;
    public int Pages { get; private set; } = 1;
    public List<AdminMovieRow> Items { get; private set; } = new();
    public List<Source> Sources { get; private set; } = new();
    public Dictionary<string, string?> QueryMap => new() { ["q"] = Q, ["source"] = Source?.ToString(), ["match"] = Match?.ToString() };

    public async Task OnGetAsync(string? q, int? source, MatchStatus? match, [FromQuery(Name = "page")] int page = 1)  // explicit: Razor Pages reserves the route value "page", so an unannotated parameter never sees ?page=N
    {
        var ct = HttpContext.RequestAborted;
        Q = q; Source = source; Match = match;
        Sources = await _db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);

        var query = Filtered(_db.Movies.AsNoTracking(), q, source, match);

        Total = await query.CountAsync(ct);
        Pages = Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
        PageNo = Math.Clamp(page, 1, Pages);
        Items = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Skip((PageNo - 1) * PageSize).Take(PageSize)
            .Select(m => new AdminMovieRow(m.Id, m.Title, m.ReleaseYear, m.FolderName, m.Source.Name, m.TmdbId, m.MatchStatus, m.MediaFiles.Count))
            .ToListAsync(ct);
    }

    private static IQueryable<Movie> Filtered(IQueryable<Movie> query, string? q, int? source, MatchStatus? match)
    {
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(m => EF.Functions.Like(m.Title, like, "\\") || EF.Functions.Like(m.FolderName, like, "\\"));
        }
        if (source.HasValue) query = query.Where(m => m.SourceId == source.Value);
        if (match.HasValue) query = query.Where(m => m.MatchStatus == match.Value);
        return query;
    }

    public async Task<IActionResult> OnPostDeleteSelectedAsync(int[] ids, string? q, int? source, MatchStatus? match, int pageNo = 1)
    {
        if (ids == null || ids.Length == 0) { Fail("Tick at least one entry first."); return Back(q, source, match, pageNo); }
        var distinct = ids.Distinct().Take(5000).ToArray();
        var removed = await CatalogCleaner.DeleteMoviesAsync(_db, _db.Movies.Where(m => distinct.Contains(m.Id)), HttpContext.RequestAborted);
        Ok($"Removed {removed:N0} entr{(removed == 1 ? "y" : "ies")} from the catalog. Nothing was deleted on the ISP server. A deep scan can rediscover them.");
        return Back(q, source, match, pageNo);
    }

    /// <summary>Deletes everything the current filter matches (all pages). Requires at least one filter so "delete all" is never a single stray click.</summary>
    public async Task<IActionResult> OnPostDeleteMatchingAsync(string? q, int? source, MatchStatus? match)
    {
        if (string.IsNullOrWhiteSpace(q) && !source.HasValue && !match.HasValue)
        {
            Fail("Choose a source, a match status or a search text first, so you do not wipe the whole catalog by accident.");
            return Back(q, source, match, 1);
        }
        var removed = await CatalogCleaner.DeleteMoviesAsync(_db, Filtered(_db.Movies, q, source, match), HttpContext.RequestAborted);
        Ok($"Removed {removed:N0} entr{(removed == 1 ? "y" : "ies")} matching the filter. Nothing was deleted on the ISP server.");
        return Back(q, source, match, 1);
    }

    // Built by hand: a route value named "page" is the Razor page name in RedirectToPage, so it cannot carry the page number.
    private IActionResult Back(string? q, int? source, MatchStatus? match, int pageNo)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(q)) parts.Add("q=" + Uri.EscapeDataString(q));
        if (source.HasValue) parts.Add("source=" + source.Value);
        if (match.HasValue) parts.Add("match=" + match.Value);
        if (pageNo > 1) parts.Add("page=" + pageNo);
        return LocalRedirect("/admin/movies" + (parts.Count > 0 ? "?" + string.Join("&", parts) : ""));
    }
}
