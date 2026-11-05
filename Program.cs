using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using MovieHub.Configuration;
using MovieHub.Data;
using MovieHub.Endpoints;
using MovieHub.Infrastructure.Crawler;
using MovieHub.Infrastructure.Media;
using MovieHub.Infrastructure.Metadata;
using MovieHub.Services;

// Helper for creating the Admin:PasswordHash value:  dotnet run -- hash-password "my secret"
if (args.Length >= 2 && args[0].Equals("hash-password", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(PasswordHasher.Hash(args[1]));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var services = builder.Services;

// ---- Options (appsettings.json, user-secrets and environment variables such as Tmdb__ApiKey) ----
services.Configure<TmdbOptions>(config.GetSection(TmdbOptions.Section));
services.Configure<OmdbOptions>(config.GetSection(OmdbOptions.Section));
services.Configure<CrawlerOptions>(config.GetSection(CrawlerOptions.Section));
services.Configure<MediaOptions>(config.GetSection(MediaOptions.Section));
services.Configure<AdminOptions>(config.GetSection(AdminOptions.Section));
services.Configure<SeedOptions>(config.GetSection(SeedOptions.Section));
services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

var crawlerOpts = config.GetSection(CrawlerOptions.Section).Get<CrawlerOptions>() ?? new CrawlerOptions();
var adminOpts = config.GetSection(AdminOptions.Section).Get<AdminOptions>() ?? new AdminOptions();

// ---- Database ----
var connectionString = config.GetConnectionString("Default") ?? "Data Source=moviehub.db;Default Timeout=30";
services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connectionString));
// Scoped context for request-bound services (pages); background work uses the factory directly.
services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// ---- HTTP clients (redirects are followed manually so every hop can be validated) ----
SocketsHttpHandler NoRedirectHandler() => new()
{
    AllowAutoRedirect = false,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
};

services.AddHttpClient(DirectoryListingClient.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(Math.Max(5, crawlerOpts.RequestTimeoutSeconds));
    c.DefaultRequestHeaders.UserAgent.ParseAdd("MovieHub/1.0");
}).ConfigurePrimaryHttpMessageHandler(NoRedirectHandler);

services.AddHttpClient(MediaEndpoints.StreamClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(NoRedirectHandler);

services.AddHttpClient(TmdbClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
services.AddHttpClient(OmdbClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));

// ---- Application services ----
services.AddMemoryCache();
services.AddSingleton<SourcePolicy>();
services.AddSingleton<MediaKinds>();
services.AddSingleton<DirectoryListingClient>();
services.AddSingleton<TmdbClient>();
services.AddSingleton<OmdbClient>();
services.AddSingleton<ToolDirectories>();
services.AddSingleton<FfprobeRunner>();
services.AddSingleton<FfmpegTool>();
services.AddSingleton<MediaInspectionService>();
services.AddSingleton<InspectionBackfill>();
services.AddSingleton<FfmpegInstaller>();
services.AddHttpClient(FfmpegInstaller.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(15));
services.AddSingleton<GenreResolver>();
services.AddSingleton<MetadataService>();
services.AddSingleton<CrawlerService>();
services.AddSingleton<ScanCoordinator>();
services.AddSingleton<CategoryService>();
services.AddSingleton<AdminAuthService>();
services.AddSingleton<LoginThrottle>();
services.AddSingleton<IMediaLookup, EfMediaLookup>();
services.AddScoped<MovieService>();

// ---- Authentication for /admin ----
services.AddAuthentication(AdminAuthDefaults.Scheme)
    .AddCookie(AdminAuthDefaults.Scheme, o =>
    {
        o.LoginPath = "/admin/login";
        o.AccessDeniedPath = "/admin/login";
        o.Cookie.Name = "moviehub.admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, adminOpts.SessionHours));
    });
services.AddAuthorization();

services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Admin");
    o.Conventions.AllowAnonymousToPage("/Admin/Login");
});

var app = builder.Build();

app.Services.GetRequiredService<FfmpegTool>().Detect();
app.Services.GetRequiredService<FfprobeRunner>().Detect();
await DbInitializer.InitializeAsync(app.Services);
await app.Services.GetRequiredService<SourcePolicy>().RefreshAsync(app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(), CancellationToken.None);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    // The movie edit page is shown inside a popup (iframe) on the admin Movies list; every other page may not be framed at all.
    ctx.Response.Headers["X-Frame-Options"] = ctx.Request.Path.StartsWithSegments("/admin/movies/edit") ? "SAMEORIGIN" : "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapMediaEndpoints();

app.Run();
