using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;
using MovieHub.Data;
using MovieHub.Infrastructure.Crawler;
using MovieHub.Models;

namespace MovieHub.Services;

/// <summary>
/// Decides when scans start and guarantees the concurrency rules: at most one scan per source at any moment,
/// a global cap on simultaneous scans, no scans unless someone asked (a category visit or an admin action),
/// and no tight retry loops against a failing source. The crawler itself lives in <see cref="CrawlerService"/>.
/// </summary>
public sealed class ScanCoordinator
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CrawlerService _crawler;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly CrawlerOptions _opt;
    private readonly ILogger<ScanCoordinator> _log;

    private readonly ConcurrentDictionary<int, ScanProgress> _running = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastVisitCheck = new();
    private readonly SemaphoreSlim _global;

    public ScanCoordinator(
        IDbContextFactory<AppDbContext> factory, CrawlerService crawler, IHostApplicationLifetime lifetime,
        IOptions<CrawlerOptions> opt, ILogger<ScanCoordinator> log)
    {
        _factory = factory;
        _crawler = crawler;
        _lifetime = lifetime;
        _opt = opt.Value;
        _log = log;
        _global = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrentScans));
    }

    public bool IsRunning(int sourceId) => _running.ContainsKey(sourceId);
    public ScanProgress? GetProgress(int sourceId) => _running.TryGetValue(sourceId, out var p) ? p : null;
    public IReadOnlyDictionary<int, ScanProgress> Running => _running;

    /// <summary>
    /// Starts a scan in the background. Returns false when a scan of this source is already queued or running —
    /// the TryAdd is the single atomic gate, so concurrent callers can never launch duplicates.
    /// </summary>
    public bool TryStart(int sourceId, bool deep, string trigger)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        var progress = new ScanProgress { Deep = deep, Trigger = trigger, Cts = cts };
        if (!_running.TryAdd(sourceId, progress)) { cts.Dispose(); return false; }

        _ = Task.Run(async () =>
        {
            var ct = cts.Token;
            var holdsSlot = false;
            try
            {
                progress.Phase = "Queued";
                await _global.WaitAsync(ct);
                holdsSlot = true;
                await _crawler.ScanAsync(sourceId, deep, trigger, progress, ct);
            }
            catch (OperationCanceledException) { /* stopped by an admin while queued, or shutting down; a running scan records its own Cancelled / Interrupted row */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected error while scanning source {SourceId}.", sourceId);
            }
            finally
            {
                if (holdsSlot) _global.Release();
                _running.TryRemove(sourceId, out _);
                cts.Dispose();
            }
        });
        return true;
    }

    /// <summary>Asks a queued or running scan of this source to stop. Returns false when nothing is running. The scan ends within moments and is recorded as Cancelled.</summary>
    public bool Stop(int sourceId)
    {
        if (!_running.TryGetValue(sourceId, out var p)) return false;
        p.StopRequested = true;
        p.Phase = "Stopping…";
        try { p.Cts?.Cancel(); } catch (ObjectDisposedException) { /* it finished just now */ }
        return true;
    }

    /// <summary>
    /// Called when someone opens a category page. Returns immediately: the check and any scan run in the background,
    /// so the page is always served straight from SQLite.
    /// </summary>
    public void NotifyCategoryVisit(IReadOnlyCollection<int> categoryIds)
    {
        if (categoryIds.Count == 0) return;
        var key = categoryIds.Min();
        var now = DateTime.UtcNow;

        // Cheap in-memory throttle so a busy page does not query the database on every hit.
        var last = _lastVisitCheck.GetOrAdd(key, DateTime.MinValue);
        if (now - last < TimeSpan.FromSeconds(30)) return;
        _lastVisitCheck[key] = now;

        var ids = categoryIds.ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _factory.CreateDbContextAsync(_lifetime.ApplicationStopping);
                var sources = await db.Sources.AsNoTracking()
                    .Where(s => s.IsEnabled && ids.Contains(s.CategoryId)).ToListAsync(_lifetime.ApplicationStopping);

                foreach (var s in sources)
                {
                    if (!ScanDecision.IsDue(s, DateTime.UtcNow, _opt.FailureBackoffMinutes)) continue;
                    if (TryStart(s.Id, deep: false, trigger: "visit"))
                        _log.LogInformation("Category visit triggered a background scan of '{Source}'.", s.Name);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Category visit check failed.");
            }
        });
    }
}
