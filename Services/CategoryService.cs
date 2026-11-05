using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieHub.Data;
using MovieHub.Models;

namespace MovieHub.Services;

public static class Slugger
{
    public static string Make(string text)
    {
        var decomposed = (text ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }
        var slug = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return slug.Length == 0 ? "item" : slug;
    }
}

/// <summary>Database-driven category tree used by the navigation bar and the category pages.</summary>
public sealed class CategoryService
{
    private const string NavCacheKey = "nav-categories";
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IMemoryCache _cache;

    public CategoryService(IDbContextFactory<AppDbContext> factory, IMemoryCache cache)
    {
        _factory = factory;
        _cache = cache;
    }

    /// <summary>
    /// Categories that are shown on the site: active themselves AND with every ancestor active, so hiding a parent hides its sub-categories too.
    /// <paramref name="all"/> must contain every category (also the hidden ones).
    /// </summary>
    public static List<Category> EffectivelyActive(IReadOnlyCollection<Category> all)
    {
        var byId = all.ToDictionary(c => c.Id);
        bool Shown(Category c)
        {
            for (var i = 0; i < 50; i++) // depth guard against a corrupt cycle
            {
                if (!c.IsActive) return false;
                if (c.ParentCategoryId is not { } pid) return true;
                if (!byId.TryGetValue(pid, out var parent)) return true;
                c = parent;
            }
            return false;
        }
        return all.Where(Shown).ToList();
    }

    /// <summary>Call after any category change so the navigation picks it up immediately.</summary>
    public void Invalidate() => _cache.Remove(NavCacheKey);

    public async Task<IReadOnlyList<NavCategory>> GetNavigationAsync(CancellationToken ct = default)
    {
        var nav = await _cache.GetOrCreateAsync(NavCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            await using var db = await _factory.CreateDbContextAsync(ct);
            var every = await db.Categories.AsNoTracking()
                .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToListAsync(ct);
            var all = EffectivelyActive(every);

            NavCategory Build(Category c) => new()
            {
                Id = c.Id, Name = c.Name, Slug = c.Slug,
                Children = all.Where(x => x.ParentCategoryId == c.Id).Select(Build).ToList()
            };
            return all.Where(c => c.ParentCategoryId == null).Select(Build).ToList();
        });
        return nav ?? new List<NavCategory>();
    }

    /// <summary>Finds an active category by slug together with the ids of itself and all active descendants.</summary>
    public async Task<ResolvedCategory?> ResolveAsync(string slug, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var every = await db.Categories.AsNoTracking()
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var all = EffectivelyActive(every);

        var category = all.FirstOrDefault(c => string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (category == null) return null;

        var ids = new List<int> { category.Id };
        for (var i = 0; i < ids.Count; i++)
            ids.AddRange(all.Where(c => c.ParentCategoryId == ids[i]).Select(c => c.Id));

        return new ResolvedCategory
        {
            Category = category,
            Parent = category.ParentCategoryId == null ? null : all.FirstOrDefault(c => c.Id == category.ParentCategoryId),
            Children = all.Where(c => c.ParentCategoryId == category.Id).ToList(),
            AllIds = ids.Distinct().ToList()
        };
    }
}
