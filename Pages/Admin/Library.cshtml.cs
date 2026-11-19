using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreamApp.Configuration;
using StreamApp.Data;
using StreamApp.Infrastructure.Crawler;
using StreamApp.Models;
using StreamApp.Services;

namespace StreamApp.Pages.Admin;

public class CategoryInput
{
    public int Id { get; set; }
    [Required, StringLength(120)] public string Name { get; set; } = "";
    [StringLength(120)] public string? Slug { get; set; }
    public int? ParentCategoryId { get; set; }
    public int DisplayOrder { get; set; }
    [StringLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public class SourceInput
{
    public int Id { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [Required] public int CategoryId { get; set; }
    [Required, StringLength(1000)] public string RootUrl { get; set; } = "";
    public ScanStrategy ScanStrategy { get; set; } = ScanStrategy.Auto;
    public SourceContentType ContentType { get; set; } = SourceContentType.Automatic;
    [Range(1, 8760)] public int CooldownHours { get; set; } = 12;
    public string? IncludedFolders { get; set; }
    public string? ExcludedFolders { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed record LibSource(Source Source, int MovieCount, int Misfiled);
public sealed record LibCategory(Category Category, int Depth, bool Shown, bool HiddenByParent, int MovieCount, List<LibSource> Sources);

/// <summary>Categories and the sources that feed them, managed together on one page.</summary>
public class LibraryModel : AdminPageModel
{
    private readonly AppDbContext _db;
    private readonly ScanCoordinator _scans;
    private readonly CrawlerService _crawler;
    private readonly SourcePolicy _policy;
    private readonly CategoryService _categories;
    private readonly CrawlerOptions _opt;

    public LibraryModel(AppDbContext db, ScanCoordinator scans, CrawlerService crawler, SourcePolicy policy, CategoryService categories, IOptions<CrawlerOptions> opt)
    {
        _db = db; _scans = scans; _crawler = crawler; _policy = policy; _categories = categories; _opt = opt.Value;
    }

    // The two forms use the prefixes "Cat." and "Src.", so a post of one never validates the other.
    [BindProperty] public CategoryInput Cat { get; set; } = new();
    [BindProperty] public SourceInput Src { get; set; } = new();

    /// <summary>Which popup to open on load: "category" or "source" (when editing a row, or after a validation error).</summary>
    public string? OpenDialog { get; private set; }

    public List<Category> AllCategories { get; private set; } = new();
    public List<LibCategory> Tree { get; private set; } = new();
    public IReadOnlyDictionary<int, ScanProgress> Running => _scans.Running;
    public SourceTestResult? TestResult { get; private set; }
    public int SourceTotal => Tree.Sum(c => c.Sources.Count);

    /// <summary>
    /// Both inputs are bound on every post. The one that was not submitted is validated as an empty object and its "required" errors
    /// appear under un-prefixed keys ("Name", "RootUrl"), so only errors of the submitted form's own prefix are kept.
    /// </summary>
    private void KeepOnly(string prefix)
    {
        foreach (var k in ModelState.Keys.Where(k => !k.StartsWith(prefix + ".", StringComparison.Ordinal)).ToList()) ModelState.Remove(k);
    }

    private async Task LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        AllCategories = await _db.Categories.AsNoTracking().OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var sources = await _db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var perSource = await _db.Movies.GroupBy(m => m.SourceId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var perCategory = await _db.Movies.GroupBy(m => m.CategoryId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var catOf = sources.ToDictionary(x => x.Id, x => x.CategoryId);
        var pairs = await _db.Movies.GroupBy(m => new { m.SourceId, m.CategoryId }).Select(g => new { g.Key.SourceId, g.Key.CategoryId, N = g.Count() }).ToListAsync(ct);
        var misfiled = pairs.Where(x => catOf.TryGetValue(x.SourceId, out var c) && c != x.CategoryId)
            .GroupBy(x => x.SourceId).ToDictionary(g => g.Key, g => g.Sum(x => x.N));

        var shownIds = CategoryService.EffectivelyActive(AllCategories).Select(c => c.Id).ToHashSet();
        var byId = AllCategories.ToDictionary(c => c.Id);
        var bySource = sources.GroupBy(s => s.CategoryId).ToDictionary(g => g.Key, g => g.ToList());

        Tree = new List<LibCategory>();
        var visited = new HashSet<int>();
        void Walk(int? parentId, int depth)
        {
            foreach (var c in AllCategories.Where(x => x.ParentCategoryId == parentId || (parentId == null && x.ParentCategoryId is { } p && !byId.ContainsKey(p))))
            {
                if (!visited.Add(c.Id)) continue;
                var shown = shownIds.Contains(c.Id);
                Tree.Add(new LibCategory(c, depth, shown, !shown && c.IsActive, perCategory.GetValueOrDefault(c.Id),
                    (bySource.GetValueOrDefault(c.Id) ?? new List<Source>()).Select(s => new LibSource(s, perSource.GetValueOrDefault(s.Id), misfiled.GetValueOrDefault(s.Id))).ToList()));
                Walk(c.Id, depth + 1);
            }
        }
        Walk(null, 0);
    }

    public async Task OnGetAsync(int? editCategory, int? editSource)
    {
        await LoadAsync();
        Cat = new CategoryInput { DisplayOrder = (AllCategories.Count == 0 ? 0 : AllCategories.Max(x => x.DisplayOrder)) + 1 };
        Src = new SourceInput { CooldownHours = _opt.DefaultCooldownHours, CategoryId = AllCategories.FirstOrDefault()?.Id ?? 0 };

        if (editCategory.HasValue && AllCategories.FirstOrDefault(c => c.Id == editCategory.Value) is { } c)
        {
            Cat = new CategoryInput { Id = c.Id, Name = c.Name, Slug = c.Slug, ParentCategoryId = c.ParentCategoryId, DisplayOrder = c.DisplayOrder, Description = c.Description, IsActive = c.IsActive };
            OpenDialog = "category";
        }
        else if (editSource.HasValue && Tree.SelectMany(t => t.Sources).FirstOrDefault(x => x.Source.Id == editSource.Value)?.Source is { } s)
        {
            Src = new SourceInput
            {
                Id = s.Id, Name = s.Name, CategoryId = s.CategoryId, RootUrl = s.RootUrl, ScanStrategy = s.ScanStrategy, ContentType = s.ContentType, CooldownHours = s.CooldownHours,
                IncludedFolders = s.IncludedFolders, ExcludedFolders = s.ExcludedFolders, IsEnabled = s.IsEnabled
            };
            OpenDialog = "source";
        }
    }

    // ------------------------------------------------------------------ categories

    public async Task<IActionResult> OnPostSaveCategoryAsync()
    {
        var ct = HttpContext.RequestAborted;
        KeepOnly("Cat");
        ModelState.Remove("Cat.Slug");
        if (!ModelState.IsValid) { OpenDialog = "category"; await LoadAsync(); return Page(); }

        var slug = Slugger.Make(string.IsNullOrWhiteSpace(Cat.Slug) ? Cat.Name : Cat.Slug);
        if (await _db.Categories.AnyAsync(c => c.Slug == slug && c.Id != Cat.Id, ct))
        {
            Fail($"The slug '{slug}' is already used by another category.");
            return RedirectToPage(new { editCategory = Cat.Id == 0 ? (int?)null : Cat.Id });
        }
        if (slug is "movie" or "genres" or "search" or "admin") { Fail("That slug is reserved."); return RedirectToPage(); }

        if (Cat.ParentCategoryId.HasValue && Cat.Id != 0 && await IsDescendantAsync(Cat.ParentCategoryId.Value, Cat.Id, ct))
        {
            Fail("A category cannot be moved under itself or one of its own sub-categories.");
            return RedirectToPage(new { editCategory = Cat.Id });
        }

        Category entity;
        if (Cat.Id == 0) { entity = new Category(); _db.Categories.Add(entity); }
        else entity = await _db.Categories.FirstOrDefaultAsync(c => c.Id == Cat.Id, ct) ?? throw new InvalidOperationException("Category not found.");

        entity.Name = Cat.Name.Trim();
        entity.Slug = slug;
        entity.ParentCategoryId = Cat.ParentCategoryId;
        entity.DisplayOrder = Cat.DisplayOrder;
        entity.Description = string.IsNullOrWhiteSpace(Cat.Description) ? null : Cat.Description.Trim();
        entity.IsActive = Cat.IsActive;
        await _db.SaveChangesAsync(ct);

        _categories.Invalidate();
        Ok($"Category '{entity.Name}' saved. It is available at /movies/{entity.Slug}.");
        return RedirectToPage();
    }

    private async Task<bool> IsDescendantAsync(int candidateId, int ancestorId, CancellationToken ct)
    {
        var current = (int?)candidateId;
        for (var i = 0; i < 20 && current.HasValue; i++)
        {
            if (current == ancestorId) return true;
            current = await _db.Categories.Where(c => c.Id == current).Select(c => c.ParentCategoryId).FirstOrDefaultAsync(ct);
        }
        return false;
    }

    public async Task<IActionResult> OnPostToggleCategoryAsync(int id)
    {
        var c = await _db.Categories.FindAsync(new object[] { id }, HttpContext.RequestAborted);
        if (c != null) { c.IsActive = !c.IsActive; await _db.SaveChangesAsync(); _categories.Invalidate(); Ok($"'{c.Name}' is now {(c.IsActive ? "visible" : "hidden")}."); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteCategoryAsync(int id)
    {
        var ct = HttpContext.RequestAborted;
        var c = await _db.Categories.FindAsync(new object[] { id }, ct);
        if (c == null) return RedirectToPage();
        if (await _db.Categories.AnyAsync(x => x.ParentCategoryId == id, ct) || await _db.Sources.AnyAsync(s => s.CategoryId == id, ct) || await _db.Movies.AnyAsync(m => m.CategoryId == id, ct))
        {
            Fail("This category still has sub-categories, sources or movies. Hide it instead, or move those first.");
            return RedirectToPage();
        }
        _db.Categories.Remove(c);
        await _db.SaveChangesAsync(ct);
        _categories.Invalidate();
        Ok($"Category '{c.Name}' deleted.");
        return RedirectToPage();
    }

    // ------------------------------------------------------------------ sources

    public async Task<IActionResult> OnPostSaveSourceAsync()
    {
        var ct = HttpContext.RequestAborted;
        KeepOnly("Src");
        var rootText = (Src.RootUrl ?? "").Trim();
        if (!Uri.TryCreate(rootText, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
            ModelState.AddModelError("Src.RootUrl", "Enter an absolute http(s) URL without credentials.");
        else if (SourcePolicy.IsForbiddenHost(uri))
            ModelState.AddModelError("Src.RootUrl", "Link-local and unspecified addresses (such as 169.254.x.x) cannot be used as a source.");
        if (!ModelState.IsValid) { OpenDialog = "source"; await LoadAsync(); return Page(); }

        var root = DirectoryListingClient.NormalizeDirUri(rootText).AbsoluteUri;
        if (await _db.Sources.AnyAsync(s => s.RootUrl == root && s.Id != Src.Id, ct))
        {
            Fail("A source with this root URL is already registered.");
            return RedirectToPage();
        }
        if (!await _db.Categories.AnyAsync(c => c.Id == Src.CategoryId, ct)) { Fail("Choose a valid category."); return RedirectToPage(); }

        Source entity;
        if (Src.Id == 0) { entity = new Source(); _db.Sources.Add(entity); }
        else entity = await _db.Sources.FirstOrDefaultAsync(s => s.Id == Src.Id, ct) ?? throw new InvalidOperationException("Source not found.");

        if (Src.Id != 0 && entity.RootUrl != root && await _db.Movies.AnyAsync(m => m.SourceId == entity.Id, ct))
        {
            Fail("The root URL of a source that already has movies cannot be changed (their stored URLs would no longer match). Register a new source instead.");
            return RedirectToPage(new { editSource = entity.Id });
        }

        var oldCategoryId = Src.Id == 0 ? 0 : entity.CategoryId;
        entity.Name = Src.Name.Trim();
        entity.CategoryId = Src.CategoryId;
        entity.RootUrl = root;
        entity.ScanStrategy = Src.ScanStrategy;
        entity.ContentType = Src.ContentType;
        entity.CooldownHours = Src.CooldownHours;
        entity.IncludedFolders = string.IsNullOrWhiteSpace(Src.IncludedFolders) ? null : Src.IncludedFolders.Trim();
        entity.ExcludedFolders = string.IsNullOrWhiteSpace(Src.ExcludedFolders) ? null : Src.ExcludedFolders.Trim();
        entity.IsEnabled = Src.IsEnabled;
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

    public async Task<IActionResult> OnPostScanAsync(int id, bool deep = false)
    {
        var s = await _db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted);
        if (s == null) return RedirectToPage();
        if (!s.IsEnabled) Fail("Enable crawling for this source before scanning it.");
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

    public async Task<IActionResult> OnPostTestSourceAsync(int id)
    {
        await LoadAsync();
        Cat = new CategoryInput { DisplayOrder = (AllCategories.Count == 0 ? 0 : AllCategories.Max(x => x.DisplayOrder)) + 1 };
        Src = new SourceInput { CooldownHours = _opt.DefaultCooldownHours, CategoryId = AllCategories.FirstOrDefault()?.Id ?? 0 };
        ModelState.Clear();
        TestResult = await _crawler.TestSourceAsync(id, HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleSourceAsync(int id)
    {
        var s = await _db.Sources.FindAsync(new object[] { id }, HttpContext.RequestAborted);
        if (s != null) { s.IsEnabled = !s.IsEnabled; await _db.SaveChangesAsync(); Ok($"Crawling of '{s.Name}' is now {(s.IsEnabled ? "enabled" : "disabled")}."); }
        return RedirectToPage();
    }

    /// <summary>Removes the source together with all of its movie entries.</summary>
    public async Task<IActionResult> OnPostDeleteSourceAsync(int id)
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
    public async Task<IActionResult> OnPostClearSourceAsync(int id)
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
