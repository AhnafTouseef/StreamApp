using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieHub.Data;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Admin;

public sealed record CategoryRow(int Id, string Name, string Slug, string? ParentName, int DisplayOrder, bool IsActive, int MovieCount, int SourceCount);

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

public class CategoriesModel : AdminPageModel
{
    private readonly AppDbContext _db;
    private readonly CategoryService _categories;
    public CategoriesModel(AppDbContext db, CategoryService categories) { _db = db; _categories = categories; }

    /// <summary>Open the add/edit popup: when editing a row, or when a submitted form had validation errors.</summary>
    public bool ShowForm => Input.Id != 0 || !ModelState.IsValid;

    [BindProperty] public CategoryInput Input { get; set; } = new();
    public List<Category> All { get; private set; } = new();
    public List<CategoryRow> Rows { get; private set; } = new();

    public async Task OnGetAsync(int? edit)
    {
        await LoadAsync();
        if (edit.HasValue && All.FirstOrDefault(c => c.Id == edit.Value) is { } c)
            Input = new CategoryInput { Id = c.Id, Name = c.Name, Slug = c.Slug, ParentCategoryId = c.ParentCategoryId, DisplayOrder = c.DisplayOrder, Description = c.Description, IsActive = c.IsActive };
        else
            Input = new CategoryInput { DisplayOrder = (All.Count == 0 ? 0 : All.Max(x => x.DisplayOrder)) + 1 };
    }

    private async Task LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        All = await _db.Categories.AsNoTracking().OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var movieCounts = await _db.Movies.GroupBy(m => m.CategoryId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var sourceCounts = await _db.Sources.GroupBy(s => s.CategoryId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        Rows = All.Select(c => new CategoryRow(c.Id, c.Name, c.Slug, All.FirstOrDefault(p => p.Id == c.ParentCategoryId)?.Name, c.DisplayOrder, c.IsActive,
            movieCounts.GetValueOrDefault(c.Id), sourceCounts.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var ct = HttpContext.RequestAborted;
        ModelState.Remove("Input.Slug");
        if (!ModelState.IsValid) { await LoadAsync(); return Page(); }

        var slug = Slugger.Make(string.IsNullOrWhiteSpace(Input.Slug) ? Input.Name : Input.Slug);
        if (await _db.Categories.AnyAsync(c => c.Slug == slug && c.Id != Input.Id, ct))
        {
            Fail($"The slug '{slug}' is already used by another category.");
            return RedirectToPage(new { edit = Input.Id == 0 ? (int?)null : Input.Id });
        }
        if (slug is "movie" or "genres" or "search" or "admin") { Fail("That slug is reserved."); return RedirectToPage(); }

        if (Input.ParentCategoryId.HasValue && Input.Id != 0 && await IsDescendantAsync(Input.ParentCategoryId.Value, Input.Id, ct))
        {
            Fail("A category cannot be moved under itself or one of its own sub-categories.");
            return RedirectToPage(new { edit = Input.Id });
        }

        Category entity;
        if (Input.Id == 0) { entity = new Category(); _db.Categories.Add(entity); }
        else entity = await _db.Categories.FirstOrDefaultAsync(c => c.Id == Input.Id, ct) ?? throw new InvalidOperationException("Category not found.");

        entity.Name = Input.Name.Trim();
        entity.Slug = slug;
        entity.ParentCategoryId = Input.ParentCategoryId;
        entity.DisplayOrder = Input.DisplayOrder;
        entity.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        entity.IsActive = Input.IsActive;
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

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var c = await _db.Categories.FindAsync(new object[] { id }, HttpContext.RequestAborted);
        if (c != null) { c.IsActive = !c.IsActive; await _db.SaveChangesAsync(); _categories.Invalidate(); Ok($"'{c.Name}' is now {(c.IsActive ? "visible" : "hidden")}."); }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
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
}
