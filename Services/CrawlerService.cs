using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;
using MovieHub.Data;
using MovieHub.Infrastructure.Crawler;
using MovieHub.Infrastructure.Media;
using MovieHub.Infrastructure.Metadata;
using MovieHub.Models;

namespace MovieHub.Services;

/// <summary>Live, in-memory progress of a running scan (persisted counters go to ScanHistory).</summary>
public sealed class ScanProgress
{
    private int _total, _processed, _added, _updated, _failed;
    public string Phase { get; set; } = "Queued";
    public string? Current { get; set; }
    public bool Deep { get; init; }
    public string Trigger { get; init; } = "";
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    private volatile bool _stopRequested;
    /// <summary>True once an admin pressed Stop. Lets the crawler tell "stopped on purpose" from "application shutting down".</summary>
    public bool StopRequested { get => _stopRequested; set => _stopRequested = value; }
    internal CancellationTokenSource? Cts { get; set; }
    public int Total { get => _total; set => _total = value; }
    public int Processed => _processed;
    public int Added => _added;
    public int Updated => _updated;
    public int Failed => _failed;
    public void IncProcessed() => Interlocked.Increment(ref _processed);
    public void IncAdded() => Interlocked.Increment(ref _added);
    public void IncUpdated() => Interlocked.Increment(ref _updated);
    public void IncFailed() => Interlocked.Increment(ref _failed);
}

public sealed record SourceTestResult(bool Success, string Message, StrategySuggestion? Suggestion, IReadOnlyList<string> Sample);

/// <summary>
/// Incremental crawler. Knows nothing about Razor Pages: it is started by <see cref="ScanCoordinator"/>.
/// Every movie is committed on its own, so an aborted or failed scan never leaves partial rows behind and a retry
/// simply picks up whatever is still missing.
/// </summary>
public sealed class CrawlerService
{
    private static readonly Regex PosterNameRx = new(@"(poster|folder|cover|thumb|front|movie)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BackdropNameRx = new(@"(fanart|backdrop|background|banner|landscape)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IgnoreDirRx = new(@"^(sample|samples|extras?|featurettes?|trailers?|screens?|screenshots?|behind the scenes)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SampleFileRx = new(@"(^|[\s._\-\[\(])sample([\s._\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly DirectoryListingClient _listing;
    private readonly MediaKinds _kinds;
    private readonly MediaInspectionService _inspect;
    private readonly MetadataService _metadata;
    private readonly SourcePolicy _policy;
    private readonly CrawlerOptions _opt;
    private readonly MediaOptions _mediaOpt;
    private readonly ILogger<CrawlerService> _log;

    public CrawlerService(
        IDbContextFactory<AppDbContext> factory, DirectoryListingClient listing, MediaKinds kinds,
        MediaInspectionService inspect, MetadataService metadata, SourcePolicy policy,
        IOptions<CrawlerOptions> opt, IOptions<MediaOptions> mediaOpt, ILogger<CrawlerService> log)
    {
        _factory = factory;
        _listing = listing;
        _kinds = kinds;
        _inspect = inspect;
        _metadata = metadata;
        _policy = policy;
        _opt = opt.Value;
        _mediaOpt = mediaOpt.Value;
        _log = log;
    }

    // ================================================================== public API

    public async Task<SourceTestResult> TestSourceAsync(int sourceId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return new SourceTestResult(false, "Source not found.", null, Array.Empty<string>());

        await _policy.RefreshAsync(_factory, ct);
        try
        {
            var entries = await _listing.ListAsync(source.RootUrl, ct);
            var suggestion = StrategyDetector.Suggest(entries, _kinds);
            var sample = entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name).Take(12)
                .Select(e => (e.IsDirectory ? "[dir] " : "") + e.Name).ToList();
            return new SourceTestResult(true, $"Connected. {entries.Count} entries found.", suggestion, sample);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return new SourceTestResult(false, "Connection failed: " + ex.Message, null, Array.Empty<string>());
        }
    }

    /// <summary>Runs one scan of a source to completion. Returns the final history row. Exceptions are recorded, not thrown (except cancellation).</summary>
    public async Task<ScanHistory> ScanAsync(int sourceId, bool deep, string trigger, ScanProgress progress, CancellationToken ct)
    {
        ScanHistory history;
        Source source;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            source = await db.Sources.Include(s => s.Category).FirstAsync(s => s.Id == sourceId, ct);
            history = new ScanHistory { SourceId = sourceId, Deep = deep, Trigger = trigger, StartedAt = DateTime.UtcNow, Status = ScanRunStatus.Running };
            db.ScanHistories.Add(history);
            source.LastScanAttempt = history.StartedAt;
            source.LastScanStatus = ScanRunStatus.Running;
            await db.SaveChangesAsync(ct);
        }

        progress.Phase = "Starting";
        var status = ScanRunStatus.Succeeded;
        string? error = null;
        var discovered = 0;

        try
        {
            await _policy.RefreshAsync(_factory, ct);
            discovered = await RunScanAsync(source, history, deep, progress, ct);
            if (progress.Failed > 0) status = ScanRunStatus.Partial;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (progress.StopRequested)
            {
                status = ScanRunStatus.Cancelled;
                error = "Stopped by an admin. Movies found before stopping were kept; press Scan now to continue.";
                _log.LogInformation("Scan of source {Source} stopped by an admin.", source.Name);
            }
            else
            {
                status = ScanRunStatus.Interrupted;
                error = "Scan was interrupted (application shutting down). It will be retried.";
                _log.LogWarning("Scan of source {Source} interrupted.", source.Name);
            }
        }
        catch (Exception ex)
        {
            status = ScanRunStatus.Failed;
            error = ex.Message;
            _log.LogError(ex, "Scan of source {Source} failed.", source.Name);
        }

        // Use a fresh token: the final bookkeeping must be saved even when the scan was cancelled.
        await using (var db = await _factory.CreateDbContextAsync(CancellationToken.None))
        {
            var h = await db.ScanHistories.FirstAsync(x => x.Id == history.Id, CancellationToken.None);
            var s = await db.Sources.FirstAsync(x => x.Id == sourceId, CancellationToken.None);
            h.FinishedAt = DateTime.UtcNow;
            h.Status = status;
            h.DiscoveredCount = discovered;
            h.AddedCount = progress.Added;
            h.UpdatedCount = progress.Updated;
            h.FailedCount = progress.Failed;
            h.ErrorMessage = error;
            s.LastScanStatus = status;
            s.LastError = error;
            if (status is ScanRunStatus.Succeeded or ScanRunStatus.Partial) s.LastSuccessfulScan = h.FinishedAt;
            await db.SaveChangesAsync(CancellationToken.None);
            history = h;
        }

        progress.Phase = status.ToString();
        _log.LogInformation("Scan of {Source} finished: {Status}, discovered {Discovered}, added {Added}, updated {Updated}, failed {Failed}.",
            source.Name, status, discovered, progress.Added, progress.Updated, progress.Failed);
        return history;
    }

    // ================================================================== scan driver

    private async Task<int> RunScanAsync(Source source, ScanHistory history, bool deep, ScanProgress progress, CancellationToken ct)
    {
        var root = DirectoryListingClient.NormalizeDirUri(source.RootUrl);
        var filter = new FolderFilter(source.IncludedFolders, source.ExcludedFolders);

        progress.Phase = "Reading source root";
        var rootEntries = await _listing.ListAsync(root.AbsoluteUri, ct); // throws when the server is down: existing data stays untouched

        var strategy = source.ScanStrategy;
        if (strategy == ScanStrategy.Auto)
        {
            var suggestion = StrategyDetector.Suggest(rootEntries, _kinds);
            strategy = suggestion.Strategy;
            _log.LogInformation("Source {Source}: auto-detected strategy {Strategy} ({Reason})", source.Name, strategy, suggestion.Reason);
        }
        if (strategy == ScanStrategy.ManualFull) deep = true;

        progress.Phase = "Discovering folders";
        var dirRecords = new List<(string Url, string Name, int? Year, int Count)>();
        var units = strategy switch
        {
            ScanStrategy.YearBased => await DiscoverYearBasedAsync(source, root, rootEntries, filter, deep, dirRecords, ct),
            ScanStrategy.RecursiveDiscovery or ScanStrategy.ManualFull => await DiscoverRecursiveAsync(root, rootEntries, filter, 0, ct),
            _ => UnitBuilder.FromEntries(rootEntries, filter, _kinds, applyIncludeFilter: true)
        };

        // Several listings can mention the same folder; keep one unit per URL.
        units = units.GroupBy(u => u.Url, StringComparer.Ordinal).Select(g => g.First()).ToList();
        progress.Total = units.Count;

        // ---- compare against what we already know
        progress.Phase = "Comparing with database";
        Dictionary<string, (int Id, DateTime? Modified)> known;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            known = (await db.Movies.AsNoTracking().Where(m => m.SourceId == source.Id)
                    .Select(m => new { m.SourceFolderUrl, m.Id, m.FolderLastModified }).ToListAsync(ct))
                .GroupBy(m => m.SourceFolderUrl, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (g.First().Id, g.First().FolderLastModified), StringComparer.Ordinal);
        }

        var toAdd = new List<MovieUnit>();
        var toRefresh = new List<(MovieUnit Unit, int MovieId)>();
        var seenIds = new List<int>();
        foreach (var u in units)
        {
            if (!known.TryGetValue(u.Url, out var k)) { toAdd.Add(u); continue; }
            seenIds.Add(k.Id);

            var changed = u.Modified.HasValue && (!k.Modified.HasValue || u.Modified.Value > k.Modified.Value);
            // Without a reliable timestamp the listing comparison cannot see changes inside a folder; only a deep scan re-inspects those.
            if (changed || deep) toRefresh.Add((u, k.Id));
        }
        _log.LogInformation("Source {Source}: {Total} units listed, {New} new, {Refresh} to refresh ({Mode}).",
            source.Name, units.Count, toAdd.Count, toRefresh.Count, deep ? "deep" : "routine");

        // ---- process new folders and changed folders with bounded parallelism
        progress.Phase = "Importing movies";
        var tmdbOk = new TmdbHealth(_metadata.IsConfigured);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _opt.MaxConcurrentFolderTasks), CancellationToken = ct };

        await Parallel.ForEachAsync(toAdd, parallel, async (unit, token) =>
        {
            progress.Current = unit.Name;
            try
            {
                if (await AddMovieAsync(source, unit, tmdbOk, token)) progress.IncAdded();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                progress.IncFailed();
                _log.LogError(ex, "Failed to import folder {Folder} from {Source}.", unit.Url, source.Name);
            }
            finally { progress.IncProcessed(); await SaveProgressAsync(history.Id, progress, token); }
        });

        await Parallel.ForEachAsync(toRefresh, parallel, async (item, token) =>
        {
            progress.Current = item.Unit.Name;
            try
            {
                if (await RefreshMovieAsync(source, item.Unit, item.MovieId, token)) progress.IncUpdated();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                progress.IncFailed();
                _log.LogError(ex, "Failed to refresh folder {Folder} from {Source}.", item.Unit.Url, source.Name);
            }
            finally { progress.IncProcessed(); }
        });

        // ---- bookkeeping: remember year directories, touch discovery timestamps, retry earlier metadata failures
        progress.Phase = "Finishing";
        await RecordDirectoriesAsync(source.Id, dirRecords, ct);
        await TouchDiscoveredAsync(seenIds, ct);
        if (tmdbOk.Usable) await BackfillMetadataAsync(source.Id, tmdbOk, ct);

        return units.Count;
    }

    private async Task SaveProgressAsync(int historyId, ScanProgress p, CancellationToken ct)
    {
        if (p.Processed % 10 != 0) return; // persist counters every 10 folders so a crash still leaves a trace
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            await db.ScanHistories.Where(h => h.Id == historyId).ExecuteUpdateAsync(s => s
                .SetProperty(h => h.DiscoveredCount, p.Total)
                .SetProperty(h => h.AddedCount, p.Added)
                .SetProperty(h => h.UpdatedCount, p.Updated)
                .SetProperty(h => h.FailedCount, p.Failed), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Could not persist scan progress.");
        }
    }

    // ================================================================== discovery strategies

    /// <summary>
    /// Year-based discovery. Year folders (with or without parentheses and "&amp; Before"/"1080p" suffixes) are located,
    /// the latest year is always listed again, a newly appeared year is picked up, and older years are skipped unless
    /// <paramref name="deep"/> is set. Non-year folders (collections) are crawled only when explicitly included.
    /// </summary>
    private async Task<List<MovieUnit>> DiscoverYearBasedAsync(
        Source source, Uri root, IReadOnlyList<DirEntry> rootEntries, FolderFilter filter, bool deep,
        List<(string Url, string Name, int? Year, int Count)> dirRecords, CancellationToken ct)
    {
        var yearDirs = new List<(string Url, string Name, int Year)>();
        var collections = new List<DirEntry>();
        foreach (var e in rootEntries.Where(e => e.IsDirectory))
        {
            if (filter.IsExcluded(e.Name)) continue;
            if (YearFolder.TryParse(e.Name, out var y)) yearDirs.Add((e.Url, e.Name, y));
            else if (filter.HasIncludes && filter.IsIncluded(e.Name)) collections.Add(e);
        }

        List<int> knownYears;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            knownYears = await db.SourceDirectories.AsNoTracking()
                .Where(d => d.SourceId == source.Id && d.Year != null && d.LastScannedAt != null)
                .Select(d => d.Year!.Value).ToListAsync(ct);
        }

        var chosen = YearPlanner.Choose(yearDirs.Select(d => (d.Url, d.Year)).ToList(), knownYears, deep);
        _log.LogInformation("Source {Source}: {Count} year folders, scanning {Chosen}.", source.Name, yearDirs.Count,
            string.Join(", ", chosen.Select(c => c.Year)));

        var units = new List<MovieUnit>();
        foreach (var (url, year) in chosen)
        {
            ct.ThrowIfCancellationRequested();
            var name = yearDirs.First(d => d.Url == url).Name;
            var entries = await _listing.ListAsync(url, ct);
            var yearUnits = UnitBuilder.FromEntries(entries, filter, _kinds, applyIncludeFilter: false);
            units.AddRange(yearUnits);
            dirRecords.Add((url, name, year, yearUnits.Count));
        }

        foreach (var c in collections)
        {
            ct.ThrowIfCancellationRequested();
            var entries = await _listing.ListAsync(c.Url, ct);
            var collUnits = UnitBuilder.FromEntries(entries, filter, _kinds, applyIncludeFilter: false);
            units.AddRange(collUnits);
            dirRecords.Add((c.Url, c.Name, null, collUnits.Count));
        }
        return units;
    }

    /// <summary>Walks the tree: a folder that directly contains video files is one movie; otherwise its sub-folders are searched.</summary>
    private async Task<List<MovieUnit>> DiscoverRecursiveAsync(Uri dirUri, IReadOnlyList<DirEntry> entries, FolderFilter filter, int depth, CancellationToken ct)
    {
        var units = new List<MovieUnit>();
        var videos = entries.Where(e => !e.IsDirectory && _kinds.IsVideo(e.Name) && !SampleFileRx.IsMatch(e.Name)).ToList();

        if (depth == 0)
            units.AddRange(videos.Select(v => new MovieUnit(v.Name, v.Url, false, v.Modified, v.Size)));
        else if (videos.Count > 0)
            return new List<MovieUnit> { new(LastSegment(dirUri), dirUri.AbsoluteUri, true, entries.Max(e => e.Modified), null) };

        if (depth >= _opt.MaxRecursionDepth) return units;
        foreach (var dir in entries.Where(e => e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (filter.IsExcluded(dir.Name) || IgnoreDirRx.IsMatch(dir.Name)) continue;
            if (depth == 0 && !filter.IsIncluded(dir.Name)) continue;

            IReadOnlyList<DirEntry> children;
            try
            {
                children = await _listing.ListAsync(dir.Url, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _log.LogWarning("Skipping unreadable folder {Url}: {Message}", dir.Url, ex.Message);
                continue;
            }
            var childUnits = await DiscoverRecursiveAsync(new Uri(dir.Url), children, filter, depth + 1, ct);
            if (childUnits.Count == 1 && childUnits[0].Url == dir.Url) childUnits[0] = childUnits[0] with { Modified = dir.Modified ?? childUnits[0].Modified };
            units.AddRange(childUnits);
        }
        return units;
    }

    private static string LastSegment(Uri u) =>
        Uri.UnescapeDataString(u.AbsolutePath.TrimEnd('/').Split('/').Last());

    // ================================================================== folder inspection

    private sealed class FolderContents
    {
        public List<DirEntry> Videos { get; } = new();
        public List<DirEntry> Subtitles { get; } = new();
        public DirEntry? Poster { get; set; }
        public DirEntry? Backdrop { get; set; }
        public DateTime? Newest { get; set; }
    }

    /// <summary>Lists a movie folder (and one level of sub-folders such as "Subs") and sorts the files by kind.</summary>
    private async Task<FolderContents> InspectFolderAsync(MovieUnit unit, CancellationToken ct)
    {
        var c = new FolderContents();
        if (!unit.IsFolder)
        {
            c.Videos.Add(new DirEntry(unit.Name, unit.Url, false, unit.Size, unit.Modified));
            c.Newest = unit.Modified;
            // companions of a loose file live next to it
            var parent = new Uri(new Uri(unit.Url), "./");
            foreach (var e in await _listing.ListAsync(parent.AbsoluteUri, ct))
            {
                if (e.IsDirectory || !_kinds.IsSubtitle(e.Name)) continue;
                var stem = Path.GetFileNameWithoutExtension(unit.Name);
                if (Path.GetFileName(e.Name).StartsWith(stem, StringComparison.OrdinalIgnoreCase)) c.Subtitles.Add(e);
            }
            return c;
        }

        await CollectAsync(unit.Url, 0, c, ct);
        c.Newest = c.Videos.Concat(c.Subtitles).Select(e => e.Modified).Where(d => d.HasValue).DefaultIfEmpty(null).Max();
        return c;
    }

    private async Task CollectAsync(string dirUrl, int depth, FolderContents c, CancellationToken ct)
    {
        var entries = await _listing.ListAsync(dirUrl, ct);
        foreach (var e in entries)
        {
            if (e.IsDirectory) continue;
            if (_kinds.IsVideo(e.Name)) { if (!SampleFileRx.IsMatch(e.Name)) c.Videos.Add(e); }
            else if (_kinds.IsSubtitle(e.Name)) c.Subtitles.Add(e);
            else if (_kinds.IsImage(e.Name))
            {
                if (BackdropNameRx.IsMatch(e.Name)) c.Backdrop ??= e;
                else if (depth == 0 && (c.Poster == null || PosterNameRx.IsMatch(e.Name) && !PosterNameRx.IsMatch(c.Poster.Name))) c.Poster = e;
            }
        }

        if (depth >= 2) return;
        // Sub-folders: always look into them when the folder has no video of its own (nested layouts), and into subtitle folders.
        foreach (var d in entries.Where(x => x.IsDirectory))
        {
            if (IgnoreDirRx.IsMatch(d.Name)) continue;
            var isSubs = Regex.IsMatch(d.Name, @"^(subs?|subtitles?)$", RegexOptions.IgnoreCase);
            if (isSubs || c.Videos.Count == 0)
                await CollectAsync(d.Url, depth + 1, c, ct);
        }
    }

    // ================================================================== add / refresh a movie

    private async Task<bool> AddMovieAsync(Source source, MovieUnit unit, TmdbHealth tmdbOk, CancellationToken ct)
    {
        var contents = await InspectFolderAsync(unit, ct);
        if (contents.Videos.Count == 0)
        {
            _log.LogDebug("Folder {Folder} has no video files; skipped.", unit.Url);
            return false;
        }

        var rawName = unit.IsFolder ? unit.Name : Path.GetFileNameWithoutExtension(unit.Name);
        var parsed = FolderNameParser.Parse(rawName);
        var quality = parsed.Quality ?? contents.Videos.Select(v => FolderNameParser.Parse(Path.GetFileNameWithoutExtension(v.Name)).Quality).FirstOrDefault(q => q != null);

        var movie = new Movie
        {
            CategoryId = source.CategoryId,
            SourceId = source.Id,
            Title = parsed.Title,
            FolderName = unit.Name,
            SourceFolderUrl = unit.Url,
            ReleaseYear = parsed.Year,
            Quality = quality,
            Tags = string.IsNullOrEmpty(parsed.Tags) ? null : parsed.Tags,
            LocalPosterUrl = contents.Poster?.Url,
            PosterUrl = contents.Poster?.Url,
            BackdropUrl = contents.Backdrop?.Url,
            FolderLastModified = unit.Modified ?? contents.Newest,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LastDiscoveredAt = DateTime.UtcNow
        };

        foreach (var v in contents.Videos.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            movie.MediaFiles.Add(new MediaFile
            {
                FileName = v.Name,
                FileUrl = v.Url,
                Extension = Path.GetExtension(v.Name).ToLowerInvariant(),
                FileSize = v.Size,
                LastModified = v.Modified,
                FileFingerprint = MediaInspectionService.Fingerprint(v.Size, v.Modified)
            });
        }
        foreach (var s in contents.Subtitles) movie.Subtitles.Add(ToSubtitle(s));

        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Movies.Add(movie);

        // ffprobe (bounded per movie and globally); failures are recorded on the file and the entry is kept
        foreach (var file in movie.MediaFiles.Take(Math.Max(1, _mediaOpt.MaxFilesProbedPerMovie)))
        {
            try { await _inspect.InspectAsync(file, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                file.InspectionError = ex.Message;
                _log.LogWarning(ex, "Inspection of {File} failed.", file.FileName);
            }
        }
        movie.Runtime ??= movie.MediaFiles.Where(f => f.Duration > 0).Select(f => (int?)Math.Round(f.Duration!.Value / 60)).FirstOrDefault();

        if (tmdbOk.Usable)
        {
            var outcome = await _metadata.MatchAndApplyAsync(db, movie, parsed.Title, parsed.Year, ct);
            tmdbOk.Observe(outcome);
        }
        else
        {
            movie.MatchNote = _metadata.IsConfigured ? "TMDB unavailable during scan; will retry." : "TMDB is not configured.";
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            _log.LogInformation("Folder {Folder} was already imported by a parallel scan.", unit.Url);
            return false;
        }
    }

    private static Subtitle ToSubtitle(DirEntry s) => new()
    {
        FileName = s.Name,
        FileUrl = s.Url,
        Format = Path.GetExtension(s.Name).TrimStart('.').ToLowerInvariant(),
        Language = SubtitleConverter.GuessLanguage(s.Name)
    };

    /// <summary>Re-reads a known folder: adds new files, updates changed ones, drops files that vanished. Returns true when something changed.</summary>
    private async Task<bool> RefreshMovieAsync(Source source, MovieUnit unit, int movieId, CancellationToken ct)
    {
        var contents = await InspectFolderAsync(unit, ct); // throws when unreachable: nothing is modified
        await using var db = await _factory.CreateDbContextAsync(ct);
        var movie = await db.Movies
            .Include(m => m.MediaFiles).ThenInclude(f => f.Streams)
            .Include(m => m.Subtitles)
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.Id == movieId, ct);
        if (movie == null) return false;

        var changed = false;
        var liveUrls = contents.Videos.Select(v => v.Url).ToHashSet(StringComparer.Ordinal);

        foreach (var v in contents.Videos)
        {
            var fingerprint = MediaInspectionService.Fingerprint(v.Size, v.Modified);
            var existing = movie.MediaFiles.FirstOrDefault(f => f.FileUrl == v.Url);
            if (existing == null)
            {
                existing = new MediaFile
                {
                    FileName = v.Name, FileUrl = v.Url, Extension = Path.GetExtension(v.Name).ToLowerInvariant(),
                    FileSize = v.Size, LastModified = v.Modified, FileFingerprint = fingerprint
                };
                movie.MediaFiles.Add(existing);
                await SafeInspectAsync(existing, ct);
                changed = true;
            }
            else if (existing.FileFingerprint != fingerprint || (existing.InspectedAt == null && existing.InspectionError == null))
            {
                existing.FileSize = v.Size ?? existing.FileSize;
                existing.LastModified = v.Modified;
                existing.FileFingerprint = fingerprint;
                await SafeInspectAsync(existing, ct);
                changed = true;
            }
        }

        // The listing succeeded, so files missing from it are really gone; keep the movie row either way.
        foreach (var gone in movie.MediaFiles.Where(f => !liveUrls.Contains(f.FileUrl)).ToList())
        {
            db.MediaFiles.Remove(gone);
            changed = true;
        }

        var subUrls = contents.Subtitles.Select(s => s.Url).ToHashSet(StringComparer.Ordinal);
        foreach (var s in contents.Subtitles.Where(s => movie.Subtitles.All(x => x.FileUrl != s.Url)))
        {
            movie.Subtitles.Add(ToSubtitle(s));
            changed = true;
        }
        foreach (var gone in movie.Subtitles.Where(s => !subUrls.Contains(s.FileUrl)).ToList())
        {
            db.Subtitles.Remove(gone);
            changed = true;
        }

        if (string.IsNullOrEmpty(movie.LocalPosterUrl) && contents.Poster != null)
        {
            movie.LocalPosterUrl = contents.Poster.Url;
            if (string.IsNullOrEmpty(movie.PosterUrl) || _opt.PreferFolderPoster && movie.MatchStatus != MatchStatus.Manual)
                movie.PosterUrl = contents.Poster.Url;
            changed = true;
        }

        movie.FolderLastModified = unit.Modified ?? contents.Newest ?? movie.FolderLastModified;
        movie.LastDiscoveredAt = DateTime.UtcNow;
        if (changed) movie.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return changed;
    }

    private async Task SafeInspectAsync(MediaFile file, CancellationToken ct)
    {
        try { await _inspect.InspectAsync(file, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            file.InspectionError = ex.Message;
            _log.LogWarning(ex, "Inspection of {File} failed.", file.FileName);
        }
    }

    // ================================================================== bookkeeping

    private async Task RecordDirectoriesAsync(int sourceId, List<(string Url, string Name, int? Year, int Count)> records, CancellationToken ct)
    {
        if (records.Count == 0) return;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.SourceDirectories.Where(d => d.SourceId == sourceId).ToListAsync(ct);
        foreach (var r in records)
        {
            var row = existing.FirstOrDefault(d => d.Url == r.Url);
            if (row == null)
            {
                row = new SourceDirectory { SourceId = sourceId, Url = r.Url };
                db.SourceDirectories.Add(row);
            }
            row.Name = r.Name;
            row.Year = r.Year;
            row.ItemCount = r.Count;
            row.LastScannedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task TouchDiscoveredAsync(List<int> ids, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(ct);
        foreach (var chunk in ids.Chunk(500))
        {
            var part = chunk.ToList();
            await db.Movies.Where(m => part.Contains(m.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastDiscoveredAt, now), ct);
        }
    }

    /// <summary>Retries TMDB for entries that could not be matched earlier (e.g. TMDB was offline), a few per scan.</summary>
    private async Task BackfillMetadataAsync(int sourceId, TmdbHealth tmdbOk, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pending = await db.Movies
            .Where(m => m.SourceId == sourceId && m.MatchStatus == MatchStatus.Unmatched && (m.MetadataFetchedAt == null || m.MetadataFetchedAt < cutoff)
                        && m.MatchNote != null && m.MatchNote.StartsWith("TMDB"))
            .OrderBy(m => m.UpdatedAt).Take(25).ToListAsync(ct);

        foreach (var movie in pending)
        {
            if (!tmdbOk.Usable) break;
            var parsed = FolderNameParser.Parse(movie.FolderName);
            var outcome = await _metadata.MatchAndApplyAsync(db, movie, parsed.Title, parsed.Year ?? movie.ReleaseYear, ct);
            tmdbOk.Observe(outcome);
            movie.MetadataFetchedAt = DateTime.UtcNow;
        }
        if (pending.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>Stops hammering TMDB for the rest of a scan after repeated failures (bad key, outage).</summary>
    private sealed class TmdbHealth
    {
        private int _failures;
        private readonly bool _configured;
        public TmdbHealth(bool configured) => _configured = configured;
        public bool Usable => _configured && Volatile.Read(ref _failures) < 3;
        public void Observe(MatchOutcome o)
        {
            if (o.Status == MatchStatus.Unmatched && o.Note.StartsWith("TMDB lookup failed", StringComparison.Ordinal))
                Interlocked.Increment(ref _failures);
        }
    }
}
