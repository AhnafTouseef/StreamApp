using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StreamApp.Configuration;
using StreamApp.Models;

namespace StreamApp.Data;

/// <summary>Creates/migrates the database, recovers from interrupted scans and seeds the category tree and configured sources.</summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var seed = services.GetRequiredService<IOptions<SeedOptions>>().Value;
        var crawler = services.GetRequiredService<IOptions<CrawlerOptions>>().Value;

        await using var db = await factory.CreateDbContextAsync(ct);

        if (db.Database.GetMigrations().Any())
        {
            await db.Database.MigrateAsync(ct);
        }
        else
        {
            log.LogWarning("No EF migrations found in the project; creating the schema with EnsureCreated(). " +
                           "Run 'dotnet ef migrations add InitialCreate' to switch to migrations (see README).");
            await db.Database.EnsureCreatedAsync(ct);
        }

        await SchemaUpgrade.ApplyAsync(db, log, ct);   // additive only: new tables/columns for features added after the database was created

        try
        {
            // WAL lets page reads proceed while the crawler writes.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not enable SQLite WAL mode; continuing with the default journal mode.");
        }

        await RecoverInterruptedScansAsync(db, log, ct);
        await SeedCategoriesAsync(db, log, ct);
        await SeedSourcesAsync(db, seed, crawler, log, ct);
    }

    private static async Task RecoverInterruptedScansAsync(AppDbContext db, ILogger log, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var stale = await db.ScanHistories.Where(h => h.Status == ScanRunStatus.Running).ToListAsync(ct);
        foreach (var h in stale)
        {
            h.Status = ScanRunStatus.Interrupted;
            h.FinishedAt = now;
            h.ErrorMessage = "The application stopped while this scan was running. It will be retried automatically.";
        }

        var sources = await db.Sources.Where(s => s.LastScanStatus == ScanRunStatus.Running).ToListAsync(ct);
        foreach (var s in sources) s.LastScanStatus = ScanRunStatus.Interrupted;

        if (stale.Count > 0 || sources.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogWarning("Marked {Count} scan(s) as interrupted after an unclean shutdown; they will be retried.", Math.Max(stale.Count, sources.Count));
        }
    }

    private static async Task SeedCategoriesAsync(AppDbContext db, ILogger log, CancellationToken ct)
    {
        if (await db.Categories.AnyAsync(ct)) return;

        var south = new Category
        {
            Name = "South Indian Movies", Slug = "south-indian", DisplayOrder = 4,
            Description = "South Indian cinema, original and Hindi dubbed.",
            Children =
            {
                new Category { Name = "Hindi Dubbed", Slug = "hindi-dubbed", DisplayOrder = 1, Description = "South Indian movies dubbed in Hindi." },
                new Category { Name = "South Movies", Slug = "south-movies", DisplayOrder = 2, Description = "South Indian movies in their original languages." }
            }
        };

        db.Categories.AddRange(
            new Category { Name = "English Movies", Slug = "english", DisplayOrder = 1, Description = "English-language movies." },
            new Category { Name = "Hindi Movies", Slug = "hindi", DisplayOrder = 2, Description = "Hindi movies." },
            new Category { Name = "Animation", Slug = "animation", DisplayOrder = 3, Description = "Animated movies." },
            south);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded the default category tree.");
    }

    private static async Task SeedSourcesAsync(AppDbContext db, SeedOptions seed, CrawlerOptions crawler, ILogger log, CancellationToken ct)
    {
        if (await db.Sources.AnyAsync(ct) || seed.Sources.Count == 0) return;

        foreach (var s in seed.Sources)
        {
            var category = await db.Categories.FirstOrDefaultAsync(c => c.Slug == s.CategorySlug, ct);
            if (category == null)
            {
                log.LogWarning("Seed source '{Name}' skipped: category '{Slug}' does not exist.", s.Name, s.CategorySlug);
                continue;
            }
            if (!Uri.TryCreate(s.RootUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                log.LogWarning("Seed source '{Name}' skipped: '{Url}' is not a valid http(s) URL.", s.Name, s.RootUrl);
                continue;
            }
            if (!Enum.TryParse<ScanStrategy>(s.Strategy, true, out var strategy)) strategy = ScanStrategy.Auto;

            db.Sources.Add(new Source
            {
                CategoryId = category.Id,
                Name = s.Name,
                RootUrl = s.RootUrl.EndsWith('/') ? s.RootUrl : s.RootUrl + "/",
                ScanStrategy = strategy,
                ContentType = Enum.TryParse<SourceContentType>(s.ContentType, true, out var ct2) ? ct2 : SourceContentType.Automatic,
                CooldownHours = s.CooldownHours ?? crawler.DefaultCooldownHours,
                IncludedFolders = s.IncludedFolders,
                ExcludedFolders = s.ExcludedFolders,
                IsEnabled = s.Enabled
            });
        }
        await db.SaveChangesAsync(ct);
        log.LogInformation("Seeded {Count} configured source(s). No movies are seeded; the catalog fills through scans.", seed.Sources.Count);
    }
}
