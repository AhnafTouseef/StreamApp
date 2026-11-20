using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StreamApp.Data;
using StreamApp.Models;
using StreamApp.Services;

namespace StreamApp.Pages.Admin;

public class AddContentInput
{
    [Required(ErrorMessage = "Choose a source.")] public int SourceId { get; set; }
    [Required(ErrorMessage = "Enter the folder URL."), StringLength(2000)] public string Url { get; set; } = "";
    public int? TmdbId { get; set; }
    [StringLength(400)] public string? Title { get; set; }
    public int? Year { get; set; }
}

/// <summary>
/// Manual registration of content the crawler missed: "Add Movie" and "Add Series" are independent pages built on this class. Nothing has to be
/// discovered first - the administrator names a folder of a source - and the entry goes through the crawler's own folder reading, ffprobe
/// inspection and TMDB matching, so playback works exactly as for crawled content.
/// </summary>
public abstract class AddContentModel : AdminPageModel
{
    private readonly AppDbContext _db;
    private readonly CrawlerService _crawler;

    protected AddContentModel(AppDbContext db, CrawlerService crawler) { _db = db; _crawler = crawler; }

    public abstract CatalogType Kind { get; }
    public bool IsSeries => Kind == CatalogType.Series;
    public string ListUrl => IsSeries ? "/admin/series" : "/admin/movies";
    public string Noun => IsSeries ? "series" : "movie";

    [BindProperty] public AddContentInput Input { get; set; } = new();
    public List<Source> Sources { get; private set; } = new();

    private async Task LoadAsync() => Sources = await _db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(HttpContext.RequestAborted);

    public async Task OnGetAsync(int? source)
    {
        await LoadAsync();
        Input = new AddContentInput { SourceId = source ?? Sources.FirstOrDefault()?.Id ?? 0 };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAsync();
        if (!ModelState.IsValid) return Page();
        var ct = HttpContext.RequestAborted;

        var source = await _db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == Input.SourceId, ct);
        if (source == null) { ModelState.AddModelError("Input.SourceId", "Choose a source."); return Page(); }
        if (!TryResolve(source, Input.Url, out var url, out var error)) { ModelState.AddModelError("Input.Url", error); return Page(); }

        var reg = new RegisterOptions(Input.TmdbId is > 0 ? Input.TmdbId : null, string.IsNullOrWhiteSpace(Input.Title) ? null : Input.Title.Trim(), Input.Year);
        (int? Id, bool Created, string Message) result;
        try
        {
            result = IsSeries ? await _crawler.RegisterSeriesAsync(source, url, reg, ct) : await _crawler.RegisterMovieAsync(source, url, reg, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            ModelState.AddModelError("Input.Url", "Could not read that location: " + ex.Message);
            return Page();
        }

        if (result.Id == null) { ModelState.AddModelError("Input.Url", result.Message); return Page(); }
        if (result.Created) Ok($"{(IsSeries ? "Series" : "Movie")} registered. Review its TMDB match below.");
        else Fail(result.Message);
        return Redirect($"{ListUrl}/edit/{result.Id}");
    }

    /// <summary>Accepts a full URL or a path below the source's root, and only locations inside that source (same host, under its root path).</summary>
    private static bool TryResolve(Source source, string input, out string url, out string error)
    {
        url = ""; error = "";
        input = input.Trim();
        if (!Uri.TryCreate(source.RootUrl.EndsWith('/') ? source.RootUrl : source.RootUrl + "/", UriKind.Absolute, out var root)) { error = "The source's root URL is invalid."; return false; }
        Uri? target;
        if (Uri.TryCreate(input, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https") target = abs;
        else if (!Uri.TryCreate(root, input.TrimStart('/'), out target)) { error = "That is not a valid URL or path."; return false; }

        var sameHost = string.Equals(target.Host, root.Host, StringComparison.OrdinalIgnoreCase) && target.Port == root.Port;
        var below = target.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.OrdinalIgnoreCase) || (target.AbsolutePath + "/").StartsWith(root.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        if (!sameHost || !below) { error = $"The location must be inside the source's root ({source.RootUrl})."; return false; }
        url = target.AbsoluteUri;
        return true;
    }
}
