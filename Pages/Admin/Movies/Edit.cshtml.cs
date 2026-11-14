using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StreamApp.Data;
using StreamApp.Infrastructure.Crawler;
using StreamApp.Infrastructure.Media;
using StreamApp.Infrastructure.Metadata;
using StreamApp.Models;

namespace StreamApp.Pages.Admin.Movies;

public class MovieInput
{
    [Required, StringLength(400)] public string Title { get; set; } = "";
    [StringLength(400)] public string? OriginalTitle { get; set; }
    public int? ReleaseYear { get; set; }
    public int? Runtime { get; set; }
    public int CategoryId { get; set; }
    [Range(0, 10)] public double? ImdbRating { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? Overview { get; set; }
}

public class EditModel : AdminPageModel
{
    private readonly AppDbContext _db;
    private readonly MetadataService _metadata;
    private readonly MediaInspectionService _inspect;
    private readonly ILogger<EditModel> _log;

    public EditModel(AppDbContext db, MetadataService metadata, MediaInspectionService inspect, ILogger<EditModel> log)
    {
        _db = db; _metadata = metadata; _inspect = inspect; _log = log;
    }

    /// <summary>True when the page is shown inside the popup on the Movies list (an iframe request, or ?embed=1).</summary>
    public bool Embed => string.Equals(Request.Headers["Sec-Fetch-Dest"], "iframe", StringComparison.OrdinalIgnoreCase) || Request.Query.ContainsKey("embed");

    public Movie Movie { get; private set; } = null!;
    public List<Category> Categories { get; private set; } = new();
    [BindProperty] public MovieInput Input { get; set; } = new();
    public List<TmdbSearchItem>? Candidates { get; private set; }
    public string? SearchTitle { get; private set; }
    public string? SearchYear { get; private set; }

    private async Task<bool> LoadAsync(int id)
    {
        var m = await _db.Movies.Include(x => x.Source).Include(x => x.Subtitles)
            .Include(x => x.MediaFiles).ThenInclude(f => f.Streams).AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (m == null) return false;
        Movie = m;
        Categories = await _db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(HttpContext.RequestAborted);
        SearchTitle ??= FolderNameParser.Parse(m.FolderName).Title;
        SearchYear ??= (FolderNameParser.Parse(m.FolderName).Year ?? m.ReleaseYear)?.ToString();
        return true;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        Input = new MovieInput
        {
            Title = Movie.Title, OriginalTitle = Movie.OriginalTitle, ReleaseYear = Movie.ReleaseYear, Runtime = Movie.Runtime, CategoryId = Movie.CategoryId,
            ImdbRating = Movie.ImdbRating, PosterUrl = Movie.PosterUrl, BackdropUrl = Movie.BackdropUrl, Overview = Movie.Overview
        };
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!ModelState.IsValid) return Page();

        var m = Movie;
        m.Title = Input.Title.Trim();
        m.OriginalTitle = Clean(Input.OriginalTitle);
        m.ReleaseYear = Input.ReleaseYear;
        m.Runtime = Input.Runtime;
        m.CategoryId = Input.CategoryId;
        m.ImdbRating = Input.ImdbRating;
        m.PosterUrl = Clean(Input.PosterUrl);
        m.BackdropUrl = Clean(Input.BackdropUrl);
        m.Overview = Clean(Input.Overview);
        if (m.MatchStatus is MatchStatus.Unmatched or MatchStatus.Ambiguous) m.MatchStatus = MatchStatus.Manual;
        m.MatchNote = m.TmdbId.HasValue ? $"TMDB #{m.TmdbId} (edited)" : "Edited manually.";
        m.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        Ok("Saved.");
        return RedirectToPage(new { id });
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<IActionResult> OnPostSearchTmdbAsync(int id, string? searchTitle, string? searchYear)
    {
        if (!await LoadAsync(id)) return NotFound();
        SearchTitle = searchTitle; SearchYear = searchYear;
        Input = new MovieInput
        {
            Title = Movie.Title, OriginalTitle = Movie.OriginalTitle, ReleaseYear = Movie.ReleaseYear, Runtime = Movie.Runtime, CategoryId = Movie.CategoryId,
            ImdbRating = Movie.ImdbRating, PosterUrl = Movie.PosterUrl, BackdropUrl = Movie.BackdropUrl, Overview = Movie.Overview
        };
        try
        {
            int? year = int.TryParse(searchYear, out var y) ? y : null;
            Candidates = await _metadata.SearchForAdminAsync(searchTitle ?? "", year, HttpContext.RequestAborted);
        }
        catch (TmdbException ex)
        {
            Fail("TMDB search failed: " + ex.Message);
            Candidates = null;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostApplyTmdbAsync(int id, int? tmdbId)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (tmdbId is not > 0) { Fail("Enter a TMDB id."); return RedirectToPage(new { id }); }
        try
        {
            await _metadata.ApplyTmdbIdAsync(_db, Movie, tmdbId.Value, MatchStatus.Manual, HttpContext.RequestAborted);
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            Ok($"Metadata updated from TMDB #{tmdbId}.");
        }
        catch (TmdbException ex) { Fail("TMDB: " + ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRefetchAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (Movie.TmdbId is not { } tmdb) return RedirectToPage(new { id });
        try
        {
            await _metadata.ApplyTmdbIdAsync(_db, Movie, tmdb, Movie.MatchStatus == MatchStatus.Manual ? MatchStatus.Manual : MatchStatus.Matched, HttpContext.RequestAborted);
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            Ok("Metadata refreshed from TMDB.");
        }
        catch (TmdbException ex) { Fail("TMDB: " + ex.Message); }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostClearMatchAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        await _db.Entry(Movie).Collection(m => m.MovieGenres).LoadAsync();
        Movie.MovieGenres.Clear();
        MetadataService.ClearMatch(Movie, FolderNameParser.Parse(Movie.FolderName));
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        Ok("TMDB match removed. Poster, overview and other TMDB fields were kept; edit them below if needed.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReinspectAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        var ok = 0; var failed = 0;
        foreach (var f in Movie.MediaFiles)
        {
            try
            {
                await _inspect.InspectAsync(f, HttpContext.RequestAborted);
                if (f.InspectionError == null) ok++; else failed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                f.InspectionError = ex.Message; failed++;
                _log.LogWarning(ex, "Re-inspection of {File} failed.", f.FileName);
            }
        }
        await _db.SaveChangesAsync(HttpContext.RequestAborted);
        if (failed == 0) Ok($"Inspected {ok} file(s).");
        else Fail($"{ok} file(s) inspected, {failed} failed. See the per-file error below.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var m = await _db.Movies.FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (m != null)
        {
            _db.Movies.Remove(m); // cascades to files, streams, subtitles and genre links; the ISP server is never contacted
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            Ok($"'{m.Title}' was removed from the catalog. Nothing was deleted on the ISP server. A deep scan can rediscover it.");
        }
        // Inside the popup there is no list to go back to: tell the list page to close the popup and refresh itself.
        if (Embed)
            return Content("<!doctype html><meta charset=\"utf-8\"><body style=\"font-family:system-ui;background:#0a0c10;color:#eceef3;padding:24px\">Removed.<script>parent.postMessage({ streamapp: 'edit-done' }, location.origin);</script>", "text/html");
        return RedirectToPage("/Admin/Movies/Index");
    }
}
