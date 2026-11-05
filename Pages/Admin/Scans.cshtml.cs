using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Admin;

public class ScansModel : AdminPageModel
{
    private readonly AppDbContext _db;
    private readonly ScanCoordinator _scans;
    public ScansModel(AppDbContext db, ScanCoordinator scans) { _db = db; _scans = scans; }

    public List<Source> Sources { get; private set; } = new();
    public List<ScanRow> History { get; private set; } = new();
    public IReadOnlyDictionary<int, ScanProgress> Running => _scans.Running;
    public bool AnyRunning => _scans.Running.Count > 0;

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Sources = await _db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        History = await IndexModel.Project(_db.ScanHistories.AsNoTracking().OrderByDescending(h => h.StartedAt).Take(60)).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostScanAsync(int id, bool deep = false)
    {
        var s = await _db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (s == null) return RedirectToPage();
        if (!s.IsEnabled) Fail("Enable the source before scanning it.");
        else if (_scans.TryStart(id, deep, "manual")) Ok($"{(deep ? "Deep scan" : "Scan")} of '{s.Name}' started.");
        else Fail($"A scan of '{s.Name}' is already running or queued.");
        return RedirectToPage();
    }

    public IActionResult OnPostStop(int id)
    {
        if (_scans.Stop(id)) Ok("Stopping the scan…");
        else Fail("No scan of that source is running.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryAsync(int id)
    {
        var s = await _db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (s == null) return RedirectToPage();
        if (_scans.TryStart(id, false, "retry")) Ok($"Retry of '{s.Name}' started.");
        else Fail($"A scan of '{s.Name}' is already running or queued.");
        return RedirectToPage();
    }
}
