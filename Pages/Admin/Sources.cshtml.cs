using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;
using MovieHub.Data;
using MovieHub.Infrastructure.Crawler;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Admin;

public class SourceInput
{
    public int Id { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [Required] public int CategoryId { get; set; }
    [Required, StringLength(1000)] public string RootUrl { get; set; } = "";
    public ScanStrategy ScanStrategy { get; set; } = ScanStrategy.Auto;
    [Range(1, 8760)] public int CooldownHours { get; set; } = 12;
    public string? IncludedFolders { get; set; }
    public string? ExcludedFolders { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class SourcesModel : AdminPageModel
{
    private readonly AppDbContext _db;
    private readonly ScanCoordinator _scans;
    private readonly CrawlerService _crawler;
    private readonly SourcePolicy _policy;
    private readonly CrawlerOptions _opt;

    public SourcesModel(AppDbContext db, ScanCoordinator scans, CrawlerService crawler, SourcePolicy policy, IOptions<CrawlerOptions> opt)
    {
        _db = db; _scans = scans; _crawler = crawler; _policy = policy; _opt = opt.Value;
    }

    /// <summary>Open the add/edit popup: when editing a row, or when a submitted form had validation errors.</summary>
    public bool ShowForm => Input.Id != 0 || !ModelState.IsValid;

    [BindProperty] public SourceInput Input { get; set; } = new();
    public List<Source> Sources { get; private set; } = new();
    public List<Category> Categories { get; private set; } = new();
    public Dictionary<int, int> MovieCounts { get; private set; } = new();
    /// <summary>Movies whose category differs from their source's category (a source was re-assigned after its movies were indexed).</summary>
    public Dictionary<int, int> MisfiledCounts { get; private set; } = new();
    public IReadOnlyDictionary<int, ScanProgress> Running => _scans.Running;
    public SourceTestResult? TestResult { get; private set; }

    private async Task LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        Sources = await _db.Sources.AsNoTracking().Include(s => s.Category).OrderBy(s => s.Name).ToListAsync(ct);
        Categories = await _db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        MovieCounts = await _db.Movies.GroupBy(m => m.SourceId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var catOf = Sources.ToDictionary(x => x.Id, x => x.CategoryId);
        var pairs = await _db.Movies.GroupBy(m => new { m.SourceId, m.CategoryId }).Select(g => new { g.Key.SourceId, g.Key.CategoryId, N = g.Count() }).ToListAsync(ct);
        MisfiledCounts = pairs.Where(x => catOf.TryGetValue(x.SourceId, out var c) && c != x.CategoryId)
            .GroupBy(x => x.SourceId).ToDictionary(g => g.Key, g => g.Sum(x => x.N));
    }

    public async Task OnGetAsync(int? edit)
    {
        await LoadAsync();
        var s = edit.HasValue ? Sources.FirstOrDefault(x => x.Id == edit.Value) : null;
        Input = s == null
            ? new SourceInput { CooldownHours = _opt.DefaultCooldownHours, CategoryId = Categories.FirstOrDefault()?.Id ?? 0 }
            : new SourceInput
            {
                Id = s.Id, Name = s.Name, CategoryId = s.CategoryId, RootUrl = s.RootUrl, ScanStrategy = s.ScanStrategy, CooldownHours = s.CooldownHours,
                IncludedFolders = s.IncludedFolders, ExcludedFolders = s.ExcludedFolders, IsEnabled = s.IsEnabled
            };
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var ct = HttpContext.RequestAborted;
        var rootText = (Input.RootUrl ?? "").Trim();
        if (!Uri.TryCreate(rootText, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
            ModelState.AddModelError("Input.RootUrl", "Enter an absolute http(s) URL without credentials.");
        else if (SourcePolicy.IsForbiddenHost(uri))
            ModelState.AddModelError("Input.RootUrl", "Link-local and unspecified addresses (such as 169.254.x.x) cannot be used as a source.");
        if (!ModelState.IsValid) { await LoadAsync(); return Page(); }

        var root = DirectoryListingClient.NormalizeDirUri(rootText).AbsoluteUri;
        if (await _db.Sources.AnyAsync(s => s.RootUrl == root && s.Id != Input.Id, ct))
        {
            Fail("A source with this root URL is already registered.");
            return RedirectToPage();
        }
        if (!await _db.Categories.AnyAsync(c => c.Id == Input.CategoryId, ct)) { Fail("Choose a valid category."); return RedirectToPage(); }

        Source entity;
        if (Input.Id == 0) { entity = new Source(); _db.Sources.Add(entity); }
        else entity = await _db.Sources.FirstOrDefaultAsync(s => s.Id == Input.Id, ct) ?? throw new InvalidOperationException("Source not found.");

        if (Input.Id != 0 && entity.RootUrl != root && await _db.Movies.AnyAsync(m => m.SourceId == entity.Id, ct))
        {
            Fail("The root URL of a source that already has movies cannot be changed (their stored URLs would no longer match). Register a new source instead.");
            return RedirectToPage(new { edit = entity.Id });
        }

        var oldCategoryId = Input.Id == 0 ? 0 : entity.CategoryId;
        entity.Name = Input.Name.Trim();
        entity.CategoryId = Input.CategoryId;
        entity.RootUrl = root;
        entity.ScanStrategy = Input.ScanStrategy;
        entity.CooldownHours = Input.CooldownHours;
        entity.IncludedFolders = string.IsNullOrWhiteSpace(Input.IncludedFolders) ? null : Input.IncludedFolders.Trim();
        entity.ExcludedFolders = string.IsNullOrWhiteSpace(Input.ExcludedFolders) ? null : Input.ExcludedFolders.Trim();
        entity.IsEnabled = Input.IsEnabled;
        await _db.SaveChangesAsync(ct);

        // A movie copies its category from the source when it is indexed, so re-assigning a source has to move its movies too.
        // Only movies that still sit in the old category move; a movie an admin filed elsewhere by hand stays where it is.
        var moved = 0;
        if (oldCategoryId != 0 && oldCategoryId != entity.CategoryId)
        {
            var sourceId = entity.Id; var from = oldCategoryId; var to = entity.CategoryId;
            moved = await _db.Movies.Where(m => m.SourceId == sourceId && m.CategoryId == from)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.CategoryId, to), ct);
        }

        await _policy.RefreshAsync(HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>(), ct);
        Ok(moved > 0
            ? $"Source '{entity.Name}' saved and its {moved:N0} movies were moved to the new category."
            : $"Source '{entity.Name}' saved. Use Test to check the connection, then Scan now.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(int id)
    {
        await LoadAsync();
        Input = new SourceInput { CooldownHours = _opt.DefaultCooldownHours, CategoryId = Categories.FirstOrDefault()?.Id ?? 0 };
        TestResult = await _crawler.TestSourceAsync(id, HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var s = await _db.Sources.FindAsync(new object[] { id }, HttpContext.RequestAborted);
        if (s != null) { s.IsEnabled = !s.IsEnabled; await _db.SaveChangesAsync(); Ok($"Crawling of '{s.Name}' is now {(s.IsEnabled ? "enabled" : "disabled")}."); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var ct = HttpContext.RequestAborted;
        var s = await _db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s == null) return RedirectToPage();
        if (_scans.IsRunning(id)) { Fail("A scan of this source is running. Wait for it to finish."); return RedirectToPage(); }
        var name = s.Name;
        var removed = await CatalogCleaner.DeleteSourceAsync(_db, id, ct);
        await _policy.RefreshAsync(HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AppDbContext>>(), ct);
        Ok($"Source '{name}' removed together with {removed:N0} movie entries. Nothing was deleted on the ISP server.");
        return RedirectToPage();
    }

    /// <summary>Wipes the movie entries of a source and its scan memory, keeping the source, so it can be rescanned from scratch.</summary>
    public async Task<IActionResult> OnPostClearAsync(int id)
    {
        var ct = HttpContext.RequestAborted;
        var s = await _db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s == null) return RedirectToPage();
        if (_scans.IsRunning(id)) { Fail("A scan of this source is running. Wait for it to finish."); return RedirectToPage(); }
        var name = s.Name;
        var removed = await CatalogCleaner.ClearSourceAsync(_db, id, ct);
        Ok($"Removed {removed:N0} movie entries of '{name}'. Press Scan now to index it again from scratch. Nothing was deleted on the ISP server.");
        return RedirectToPage();
    }

    /// <summary>Files every movie of the source under the source's current category (fixes a source that was re-assigned earlier).</summary>
    public async Task<IActionResult> OnPostSyncCategoryAsync(int id)
    {
        var ct = HttpContext.RequestAborted;
        var s = await _db.Sources.AsNoTracking().Include(x => x.Category).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s == null) return RedirectToPage();
        var to = s.CategoryId;
        var moved = await _db.Movies.Where(m => m.SourceId == id && m.CategoryId != to)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.CategoryId, to), ct);
        Ok($"{moved:N0} movies of '{s.Name}' were moved to '{s.Category.Name}'.");
        return RedirectToPage();
    }
}
