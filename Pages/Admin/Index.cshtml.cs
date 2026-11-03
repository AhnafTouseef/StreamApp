using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Admin;

public sealed record RecentMovie(int Id, string Title, int? ReleaseYear, string SourceName, MatchStatus MatchStatus, DateTime CreatedAt);
public sealed record ScanRow(int Id, string SourceName, DateTime StartedAt, DateTime? FinishedAt, ScanRunStatus Status, bool Deep, string Trigger,
    int DiscoveredCount, int AddedCount, int UpdatedCount, int FailedCount, string? ErrorMessage);

public class IndexModel : AdminPageModel
{
    private readonly AppDbContext _db;
    private readonly ScanCoordinator _scans;
    public IndexModel(AppDbContext db, ScanCoordinator scans) { _db = db; _scans = scans; }

    public int TotalMovies { get; private set; }
    public int TotalCategories { get; private set; }
    public int TotalSources { get; private set; }
    public DateTime? LastSuccess { get; private set; }
    public bool AnyRunning => _scans.Running.Count > 0;
    public List<RecentMovie> Recent { get; private set; } = new();
    public List<ScanRow> History { get; private set; } = new();
    public List<ScanRow> Failures { get; private set; } = new();

    public static IQueryable<ScanRow> Project(IQueryable<ScanHistory> q) => q.Select(h => new ScanRow(
        h.Id, h.Source.Name, h.StartedAt, h.FinishedAt, h.Status, h.Deep, h.Trigger,
        h.DiscoveredCount, h.AddedCount, h.UpdatedCount, h.FailedCount, h.ErrorMessage));

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        TotalMovies = await _db.Movies.CountAsync(ct);
        TotalCategories = await _db.Categories.CountAsync(ct);
        TotalSources = await _db.Sources.CountAsync(ct);
        LastSuccess = await _db.Sources.MaxAsync(s => s.LastSuccessfulScan, ct);
        Recent = await _db.Movies.AsNoTracking().OrderByDescending(m => m.CreatedAt).Take(10)
            .Select(m => new RecentMovie(m.Id, m.Title, m.ReleaseYear, m.Source.Name, m.MatchStatus, m.CreatedAt)).ToListAsync(ct);
        History = await Project(_db.ScanHistories.AsNoTracking().OrderByDescending(h => h.StartedAt).Take(10)).ToListAsync(ct);
        Failures = await Project(_db.ScanHistories.AsNoTracking()
            .Where(h => h.Status == ScanRunStatus.Failed || h.Status == ScanRunStatus.Interrupted || h.Status == ScanRunStatus.Partial)
            .OrderByDescending(h => h.StartedAt).Take(10)).ToListAsync(ct);
    }
}
