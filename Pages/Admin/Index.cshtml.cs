using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Infrastructure.Media;
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
    private readonly FfprobeRunner _ffprobe;
    private readonly FfmpegTool _ffmpeg;
    private readonly InspectionBackfill _backfill;
    private readonly FfmpegInstaller _installer;
    private readonly MovieHub.Infrastructure.Metadata.MetadataService _metadata;
    public IndexModel(AppDbContext db, ScanCoordinator scans, FfprobeRunner ffprobe, FfmpegTool ffmpeg, InspectionBackfill backfill, FfmpegInstaller installer, MovieHub.Infrastructure.Metadata.MetadataService metadata)
    { _db = db; _scans = scans; _ffprobe = ffprobe; _ffmpeg = ffmpeg; _backfill = backfill; _installer = installer; _metadata = metadata; }

    public FfmpegInstaller Installer => _installer;

    public bool FfprobeOk => _ffprobe.IsAvailable;
    public string FfprobeInfo => _ffprobe.IsAvailable ? $"{_ffprobe.Command} — {_ffprobe.Version}" : "not found";
    public bool FfmpegOk => _ffmpeg.IsAvailable;
    public string FfmpegInfo => _ffmpeg.IsAvailable ? $"{_ffmpeg.Path} — {_ffmpeg.Version}" : "not found";
    public int PendingInspections { get; private set; }
    public int FolderPosterCount { get; private set; }
    public bool TmdbOk => _metadata.IsConfigured;
    public InspectionBackfill Backfill => _backfill;

    public int TotalMovies { get; private set; }
    public int TotalCategories { get; private set; }
    public int TotalSources { get; private set; }
    public DateTime? LastSuccess { get; private set; }
    public bool AnyRunning => _scans.Running.Count > 0 || _backfill.IsRunning || _installer.IsRunning;
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
        PendingInspections = await InspectionBackfill.Pending(_db.MediaFiles).CountAsync(ct);
        FolderPosterCount = await _db.Movies.CountAsync(m => m.TmdbId != null && m.LocalPosterUrl != null && m.PosterUrl == m.LocalPosterUrl, ct);
        LastSuccess = await _db.Sources.MaxAsync(s => s.LastSuccessfulScan, ct);
        Recent = await _db.Movies.AsNoTracking().OrderByDescending(m => m.CreatedAt).Take(10)
            .Select(m => new RecentMovie(m.Id, m.Title, m.ReleaseYear, m.Source.Name, m.MatchStatus, m.CreatedAt)).ToListAsync(ct);
        History = await Project(_db.ScanHistories.AsNoTracking().OrderByDescending(h => h.StartedAt).Take(10)).ToListAsync(ct);
        Failures = await Project(_db.ScanHistories.AsNoTracking()
            .Where(h => h.Status == ScanRunStatus.Failed || h.Status == ScanRunStatus.Interrupted || h.Status == ScanRunStatus.Partial)
            .OrderByDescending(h => h.StartedAt).Take(10)).ToListAsync(ct);
    }

    public IActionResult OnPostInspectPending()
    {
        if (!_ffprobe.IsAvailable) Fail("FFprobe was not found, so nothing can be inspected. Use \"Download FFmpeg\" above or copy ffprobe into the tools folder.");
        else if (_backfill.TryStartAll()) Ok("Inspecting all files that have no stream information. This page refreshes while it runs.");
        else Fail("An inspection pass is already running.");
        return RedirectToPage();
    }

    public IActionResult OnPostDownloadFfmpeg()
    {
        if (!_installer.CanInstall) Fail("Automatic download is only set up for Windows. Copy ffmpeg and ffprobe into the tools folder instead.");
        else if (_installer.TryStart()) Ok("Downloading FFmpeg into the tools folder. This page refreshes while it runs.");
        else Fail("A download is already running.");
        return RedirectToPage();
    }

    /// <summary>Movies matched on TMDB that still show the poster from the ISP folder get the TMDB poster instead (a batch per click keeps each request short).</summary>
    public async Task<IActionResult> OnPostUseTmdbPostersAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!_metadata.IsConfigured) { Fail("TMDB is not configured (set Tmdb:ApiKey)."); return RedirectToPage(); }
        var batch = await _db.Movies.Where(m => m.TmdbId != null && m.LocalPosterUrl != null && m.PosterUrl == m.LocalPosterUrl).OrderBy(m => m.Id).Take(150).ToListAsync(ct);
        int changed = 0, none = 0;
        try
        {
            foreach (var m in batch)
            {
                if (await _metadata.UseTmdbPosterAsync(m, ct)) changed++; else { none++; m.PosterUrl = m.LocalPosterUrl; }
                if (changed % 10 == 0) await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Fail("Stopped early: " + ex.Message); }
        await _db.SaveChangesAsync(ct);
        var left = await _db.Movies.CountAsync(m => m.TmdbId != null && m.LocalPosterUrl != null && m.PosterUrl == m.LocalPosterUrl, ct);
        Ok($"{changed} poster(s) switched to TMDB. {(none > 0 ? $"{none} have no TMDB poster and keep the folder poster. " : "")}{(left - none > 0 ? "More remain: press the button again." : "")}");
        return RedirectToPage();
    }

    public IActionResult OnPostRecheckTools()
    {
        _ffmpeg.Reset();
        _ffprobe.Reset();
        if (_ffmpeg.IsAvailable && _ffprobe.IsAvailable) Ok("Both tools found.");
        else Fail("Still missing: " + string.Join(" and ", new[] { _ffprobe.IsAvailable ? null : "ffprobe", _ffmpeg.IsAvailable ? null : "ffmpeg" }.Where(x => x != null)) + ".");
        return RedirectToPage();
    }
}
