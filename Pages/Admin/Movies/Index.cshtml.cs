using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Models;

namespace MovieHub.Pages.Admin.Movies;

public sealed record AdminMovieRow(int Id, string Title, int? Year, string FolderName, string SourceName, int? TmdbId, MatchStatus Match, int Files);

public class IndexModel : AdminPageModel
{
    private const int PageSize = 40;
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public string? Q { get; private set; }
    public int? Source { get; private set; }
    public MatchStatus? Match { get; private set; }
    public int Total { get; private set; }
    public int PageNo { get; private set; } = 1;
    public int Pages { get; private set; } = 1;
    public List<AdminMovieRow> Items { get; private set; } = new();
    public List<Source> Sources { get; private set; } = new();
    public Dictionary<string, string?> QueryMap => new() { ["q"] = Q, ["source"] = Source?.ToString(), ["match"] = Match?.ToString() };

    public async Task OnGetAsync(string? q, int? source, MatchStatus? match, int page = 1)
    {
        var ct = HttpContext.RequestAborted;
        Q = q; Source = source; Match = match;
        Sources = await _db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);

        var query = _db.Movies.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(m => EF.Functions.Like(m.Title, like, "\\") || EF.Functions.Like(m.FolderName, like, "\\"));
        }
        if (source.HasValue) query = query.Where(m => m.SourceId == source.Value);
        if (match.HasValue) query = query.Where(m => m.MatchStatus == match.Value);

        Total = await query.CountAsync(ct);
        Pages = Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
        PageNo = Math.Clamp(page, 1, Pages);
        Items = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Skip((PageNo - 1) * PageSize).Take(PageSize)
            .Select(m => new AdminMovieRow(m.Id, m.Title, m.ReleaseYear, m.FolderName, m.Source.Name, m.TmdbId, m.MatchStatus, m.MediaFiles.Count))
            .ToListAsync(ct);
    }
}
