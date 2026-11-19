using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using StreamApp.Configuration;
using StreamApp.Data;
using StreamApp.Infrastructure.Crawler;
using StreamApp.Infrastructure.Media;
using StreamApp.Infrastructure.Metadata;
using StreamApp.Models;

namespace StreamApp.Services;

/// <summary>Stops hammering TMDB for the rest of a scan after repeated failures (bad key, outage). Shared by the movie and series crawls.</summary>
internal sealed class TmdbHealth
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

/// <summary>One season folder of a series (or a "Show Name S02" folder that is merged into the series).</summary>
public sealed record SeasonRef(string Url, string Name, int? Number, DateTime? Modified);

/// <summary>One series to ingest. <see cref="Loose"/> are the files lying directly in the series folder (episodes of a season-less layout, specials, poster).</summary>
public sealed record SeriesUnit(string Name, string Url, DateTime? Modified, IReadOnlyList<SeasonRef> Seasons, IReadOnlyList<DirEntry> Loose, (string Title, int Number)? TitledSeason = null);

/// <summary>
/// Crawls a source that holds TV series: Series → Seasons → Episodes → Media files. Layouts are discovered, not assumed:
/// a folder that has season folders (or directly holds video files) is a series, any other folder is a grouping level (alphabet, year …) that is walked into.
/// The movie crawl is not involved; this class only borrows its building blocks (listing client, folder filter, ffprobe inspection, TMDB).
/// </summary>
public sealed class SeriesCrawler
{
    private static readonly Regex BackdropNameRx = new(@"(fanart|backdrop|background|banner|landscape)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IgnoreDirRx = new(@"^(sample|samples|extras?|featurettes?|trailers?|screens?|screenshots?|behind the scenes)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SampleFileRx = new(@"(^|[\s._\-\[\(])sample([\s._\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SubsDirRx = new(@"^(subs?|subtitles?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly DirectoryListingClient _listing;
    private readonly MediaKinds _kinds;
    private readonly MediaInspectionService _inspect;
    private readonly MetadataService _metadata;
    private readonly CrawlerOptions _opt;
    private readonly MediaOptions _mediaOpt;
    private readonly ILogger _log;

    public SeriesCrawler(IDbContextFactory<AppDbContext> factory, DirectoryListingClient listing, MediaKinds kinds, MediaInspectionService inspect,
        MetadataService metadata, CrawlerOptions opt, MediaOptions mediaOpt, ILogger log)
    {
        _factory = factory; _listing = listing; _kinds = kinds; _inspect = inspect; _metadata = metadata; _opt = opt; _mediaOpt = mediaOpt; _log = log;
    }

    // ================================================================== scan driver

    public async Task<int> RunAsync(Source source, ScanHistory history, bool deep, ScanProgress progress, Uri root,
        IReadOnlyList<DirEntry> rootEntries, FolderFilter filter, CancellationToken ct)
    {
        progress.Phase = "Discovering series";
        var units = await DiscoverAsync(root, null, rootEntries, filter, 0, ct);
        units = units.GroupBy(u => u.Url, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var listed = units.Count;
        progress.Total = units.Count;

        progress.Phase = "Comparing with database";
        Dictionary<string, (int Id, DateTime? Modified, DateTime? Activity)> known;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            known = (await db.Movies.AsNoTracking().Where(m => m.SourceId == source.Id && m.ContentType == CatalogType.Series)
                    .Select(m => new { m.SourceFolderUrl, m.Id, m.FolderLastModified, Activity = m.Series != null ? m.Series.LastActivityAt : null }).ToListAsync(ct))
                .GroupBy(m => m.SourceFolderUrl, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (g.First().Id, g.First().FolderLastModified, g.First().Activity), StringComparer.Ordinal);
        }

        // A routine scan re-reads a known series when its folder changed (a new season folder appeared) or it was active recently
        // (a new episode only changes the SEASON folder's date, which the series listing cannot show). Deep scans re-read everything.
        var cutoff = _opt.RecentWindowMonths > 0 ? DateTime.UtcNow.AddMonths(-_opt.RecentWindowMonths) : DateTime.MaxValue;
        var toAdd = new List<SeriesUnit>();
        var toRefresh = new List<(SeriesUnit Unit, int Id)>();
        var seen = new List<int>();
        foreach (var u in units)
        {
            if (!known.TryGetValue(u.Url, out var k)) { toAdd.Add(u); continue; }
            seen.Add(k.Id);
            var folderChanged = u.Modified.HasValue && (!k.Modified.HasValue || u.Modified.Value > k.Modified.Value);
            var recentlyActive = !k.Activity.HasValue || k.Activity.Value >= cutoff;
            if (deep || folderChanged || recentlyActive) toRefresh.Add((u, k.Id));
        }
        _log.LogInformation("Source {Source} (series): {Total} series listed, {New} new, {Refresh} to refresh ({Mode}).",
            source.Name, units.Count, toAdd.Count, toRefresh.Count, deep ? "deep" : "routine");
        progress.Total = toAdd.Count + toRefresh.Count;

        progress.Phase = "Importing series";
        var tmdbOk = new TmdbHealth(_metadata.IsConfigured);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _opt.MaxConcurrentFolderTasks), CancellationToken = ct };

        await Parallel.ForEachAsync(toAdd, parallel, async (unit, token) =>
        {
            progress.Current = unit.Name;
            try { if (await ProcessAsync(source, unit, null, deep, tmdbOk, token)) progress.IncAdded(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { progress.IncFailed(); _log.LogError(ex, "Failed to import series {Folder} from {Source}.", unit.Url, source.Name); }
            finally { progress.IncProcessed(); await SaveProgressAsync(history.Id, progress, token); }
        });

        await Parallel.ForEachAsync(toRefresh, parallel, async (item, token) =>
        {
            progress.Current = item.Unit.Name;
            try { if (await ProcessAsync(source, item.Unit, item.Id, deep, tmdbOk, token)) progress.IncUpdated(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { progress.IncFailed(); _log.LogError(ex, "Failed to refresh series {Folder} from {Source}.", item.Unit.Url, source.Name); }
            finally { progress.IncProcessed(); await SaveProgressAsync(history.Id, progress, token); }
        });

        progress.Phase = "Finishing";
        await TouchDiscoveredAsync(seen, ct);
        if (tmdbOk.Usable) await BackfillMetadataAsync(source.Id, tmdbOk, ct);
        return listed;
    }

    private async Task SaveProgressAsync(int historyId, ScanProgress p, CancellationToken ct)
    {
        if (p.Processed % 5 != 0) return;
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            await db.ScanHistories.Where(h => h.Id == historyId).ExecuteUpdateAsync(s => s
                .SetProperty(h => h.DiscoveredCount, p.Total).SetProperty(h => h.AddedCount, p.Added)
                .SetProperty(h => h.UpdatedCount, p.Updated).SetProperty(h => h.FailedCount, p.Failed), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "Could not persist scan progress."); }
    }

    // ================================================================== discovery

    /// <summary>Series thumbnails sit next to the season folders and there can be several: the first image (by name) is used until TMDB supplies one.</summary>
    private DirEntry? PickThumbnail(IEnumerable<DirEntry> loose) =>
        loose.Where(e => !e.IsDirectory && _kinds.IsImage(e.Name) && !BackdropNameRx.IsMatch(e.Name))
             .OrderBy(e => e.Name, NaturalComparer.Instance).FirstOrDefault();

    private static string LastSegment(string url) => Uri.UnescapeDataString(new Uri(url).AbsolutePath.TrimEnd('/').Split('/').Last());

    /// <summary>
    /// Walks the tree. A folder holding season folders or video files is a series; anything else is a grouping level and is searched deeper.
    /// "Show Name S01" / "Show Name S02" folders that sit side by side are merged into one series.
    /// </summary>
    private async Task<List<SeriesUnit>> DiscoverAsync(Uri dirUri, DateTime? dirModified, IReadOnlyList<DirEntry> entries, FolderFilter filter, int depth, CancellationToken ct)
    {
        var dirs = entries.Where(e => e.IsDirectory && !filter.IsExcluded(e.Name) && !IgnoreDirRx.IsMatch(e.Name)).ToList();
        var videos = entries.Where(e => !e.IsDirectory && _kinds.IsVideo(e.Name) && !SampleFileRx.IsMatch(e.Name)).ToList();
        var seasonDirs = dirs.Where(d => SeasonFolder.TryParse(d.Name, out _)).ToList();

        var isSeriesFolder = seasonDirs.Count > 0 || (depth > 0 && videos.Count > 0)
                             || (depth == 0 && videos.Count(v => EpisodeParser.HasSeasonEpisodeMarker(Path.GetFileNameWithoutExtension(v.Name))) >= 2);
        if (isSeriesFolder)
        {
            var name = LastSegment(dirUri.AbsoluteUri);
            var refs = seasonDirs.Select(d => { SeasonFolder.TryParse(d.Name, out var n); return new SeasonRef(d.Url, d.Name, n, d.Modified); }).ToList();
            var loose = entries.Where(e => !e.IsDirectory).ToList();
            var modified = dirModified ?? entries.Max(e => e.Modified);

            (string, int)? titled = null;
            if (depth > 0 && seasonDirs.Count == 0 && SeasonFolder.TryParseTitled(name, out var tTitle, out var tNum))
            {
                // "Show Name S02": a season folder named after its show. The merge below turns siblings into one series.
                titled = (tTitle, tNum);
                refs = new List<SeasonRef> { new(DirectoryListingClient.NormalizeDirUri(dirUri.AbsoluteUri).AbsoluteUri, name, tNum, modified) };
                loose = new List<DirEntry>();
            }
            return new List<SeriesUnit> { new(name, DirectoryListingClient.NormalizeDirUri(dirUri.AbsoluteUri).AbsoluteUri, modified, refs, loose, titled) };
        }

        var units = new List<SeriesUnit>();
        if (depth >= _opt.MaxRecursionDepth) return units;
        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (depth == 0 && !filter.IsIncluded(dir.Name)) continue;
            IReadOnlyList<DirEntry> children;
            try { children = await _listing.ListAsync(dir.Url, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _log.LogWarning("Skipping unreadable folder {Url}: {Message}", dir.Url, ex.Message);
                continue;
            }
            units.AddRange(await DiscoverAsync(new Uri(dir.Url), dir.Modified, children, filter, depth + 1, ct));
        }
        return MergeTitledSeasons(units, dirUri);
    }

    private static List<SeriesUnit> MergeTitledSeasons(List<SeriesUnit> units, Uri parent)
    {
        if (!units.Any(u => u.TitledSeason != null)) return units;
        var result = units.Where(u => u.TitledSeason == null).ToList();
        foreach (var g in units.Where(u => u.TitledSeason != null).GroupBy(u => FolderNameParser.Normalize(u.TitledSeason!.Value.Title)))
        {
            var first = g.First();
            var seasons = g.SelectMany(u => u.Seasons).OrderBy(s => s.Number).ToList();
            var url = parent.AbsoluteUri.TrimEnd('/') + "/#series:" + Uri.EscapeDataString(g.Key);
            result.Add(new SeriesUnit(first.TitledSeason!.Value.Title, url, g.Max(u => u.Modified), seasons, Array.Empty<DirEntry>()));
        }
        return result;
    }

    // ================================================================== reading a series' folders

    private sealed class Listed
    {
        public List<(DirEntry File, int? FolderSeason)> Videos { get; } = new();
        public List<DirEntry> Subtitles { get; } = new();
        public HashSet<string> VisitedDirs { get; } = new(StringComparer.Ordinal);
        public DateTime? Newest { get; set; }
    }

    private async Task CollectAsync(string dirUrl, int depth, int? folderSeason, Listed into, CancellationToken ct)
    {
        into.VisitedDirs.Add(dirUrl);
        var entries = await _listing.ListAsync(dirUrl, ct);
        foreach (var e in entries)
        {
            if (e.IsDirectory) continue;
            if (_kinds.IsVideo(e.Name)) { if (!SampleFileRx.IsMatch(e.Name)) { into.Videos.Add((e, folderSeason)); Newer(into, e.Modified); } }
            else if (_kinds.IsSubtitle(e.Name)) into.Subtitles.Add(e);
        }
        if (depth >= 3) return;
        foreach (var d in entries.Where(x => x.IsDirectory && !IgnoreDirRx.IsMatch(x.Name)))
            await CollectAsync(d.Url, depth + 1, folderSeason, into, ct);   // episodes in their own folders, "Subs" folders
    }

    private static void Newer(Listed l, DateTime? d) { if (d.HasValue && (!l.Newest.HasValue || d > l.Newest)) l.Newest = d; }

    // ================================================================== add / refresh one series

    private sealed record Placement(DirEntry File, int Season, int Number, int? End, bool Inferred);

    private async Task<bool> ProcessAsync(Source source, SeriesUnit unit, int? movieId, bool deep, TmdbHealth tmdbOk, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        Movie? movie = null;
        if (movieId.HasValue)
        {
            movie = await db.Movies
                .Include(m => m.Series!).ThenInclude(s => s.Seasons).ThenInclude(s => s.Episodes)
                .Include(m => m.MediaFiles).ThenInclude(f => f.Streams)
                .Include(m => m.Subtitles)
                .Include(m => m.MovieGenres)
                .AsSplitQuery()
                .FirstOrDefaultAsync(m => m.Id == movieId.Value, ct);
            if (movie == null) return false;
        }
        var isNew = movie == null;
        var existingSeasons = movie?.Series?.Seasons.ToDictionary(s => s.Number) ?? new Dictionary<int, Season>();

        // ---- list what has to be read: new or changed season folders only (a series folder with loose files is already listed)
        var listed = new Listed();
        var relistedSeasons = new HashSet<int>();
        foreach (var sr in unit.Seasons)
        {
            var known = sr.Number.HasValue && existingSeasons.TryGetValue(sr.Number.Value, out var s) ? s : null;
            var needsRead = isNew || deep || known == null || !sr.Modified.HasValue || !known.FolderLastModified.HasValue || sr.Modified.Value > known.FolderLastModified.Value;
            if (!needsRead) continue;
            await CollectAsync(sr.Url, 0, sr.Number, listed, ct);
            if (sr.Number.HasValue) relistedSeasons.Add(sr.Number.Value);
        }
        foreach (var e in unit.Loose)
        {
            if (_kinds.IsVideo(e.Name) && !SampleFileRx.IsMatch(e.Name)) { listed.Videos.Add((e, null)); Newer(listed, e.Modified); }
            else if (_kinds.IsSubtitle(e.Name)) listed.Subtitles.Add(e);
        }
        if (unit.Loose.Count > 0) listed.VisitedDirs.Add(unit.Url);

        if (listed.Videos.Count == 0 && isNew)
        {
            _log.LogDebug("Series folder {Folder} has no video files; skipped.", unit.Url);
            return false;
        }

        // ---- the catalog entry
        var parsed = SeriesTitle.Parse(unit.Name);
        if (isNew)
        {
            var poster = PickThumbnail(unit.Loose);
            var backdrop = unit.Loose.FirstOrDefault(e => !e.IsDirectory && _kinds.IsImage(e.Name) && BackdropNameRx.IsMatch(e.Name));
            movie = new Movie
            {
                ContentType = CatalogType.Series,
                CategoryId = source.CategoryId, SourceId = source.Id,
                Title = parsed.Title, FolderName = unit.Name, SourceFolderUrl = unit.Url, ReleaseYear = parsed.Year,
                Tags = string.IsNullOrEmpty(parsed.Tags) ? null : parsed.Tags,
                LocalPosterUrl = poster?.Url, PosterUrl = poster?.Url, BackdropUrl = backdrop?.Url,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LastDiscoveredAt = DateTime.UtcNow,
                Series = new Series()
            };
            db.Movies.Add(movie);
        }
        else
        {
            movie!.Series ??= new Series();
            if (movie.ContentType != CatalogType.Series) movie.ContentType = CatalogType.Series;
            if (string.IsNullOrEmpty(movie.LocalPosterUrl))
            {
                var poster = PickThumbnail(unit.Loose);
                if (poster != null) { movie.LocalPosterUrl = poster.Url; if (string.IsNullOrEmpty(movie.PosterUrl)) movie.PosterUrl = poster.Url; }
            }
        }
        var series = movie!.Series!;

        // ---- episode numbering for the files that are new to the database
        var existingByUrl = movie.MediaFiles.ToDictionary(f => f.FileUrl, StringComparer.Ordinal);
        var newFiles = listed.Videos.Where(v => !existingByUrl.ContainsKey(v.File.Url)).ToList();
        var placements = Plan(newFiles, unit.Seasons.Count > 0, existingSeasons);

        var changedSeasons = new HashSet<int>();
        var changed = isNew;

        // ---- existing files: refresh changed ones, drop vanished ones
        var live = listed.Videos.ToDictionary(v => v.File.Url, v => v.File, StringComparer.Ordinal);
        var probeBudget = Math.Max(1, _mediaOpt.MaxFilesProbedPerMovie);
        foreach (var f in movie.MediaFiles.Where(f => f.EpisodeId != null || f.Episode != null).ToList())
        {
            if (live.TryGetValue(f.FileUrl, out var v))
            {
                var fp = MediaInspectionService.Fingerprint(v.Size, v.Modified);
                if (f.FileFingerprint != fp)
                {
                    f.FileSize = v.Size ?? f.FileSize; f.LastModified = v.Modified; f.FileFingerprint = fp;
                    if (probeBudget > 0) { probeBudget--; await SafeInspectAsync(f, ct); } else { f.InspectedAt = null; f.InspectionError = null; }
                    changed = true;
                }
            }
            else if (listed.VisitedDirs.Contains(ParentDir(f.FileUrl)))
            {
                var ep = f.Episode ?? series.Seasons.SelectMany(s => s.Episodes).FirstOrDefault(e => e.Id == f.EpisodeId);
                db.MediaFiles.Remove(f);
                movie.MediaFiles.Remove(f);
                if (ep != null) { ep.MediaFiles.Remove(f); }
                changed = true;
            }
        }

        // ---- new files → seasons / episodes
        var seasonRows = series.Seasons.ToDictionary(s => s.Number);
        Season SeasonFor(int number)
        {
            if (seasonRows.TryGetValue(number, out var s)) return s;
            var sr = unit.Seasons.FirstOrDefault(x => x.Number == number);
            s = new Season { Number = number, FolderName = sr?.Name, FolderUrl = sr?.Url ?? unit.Url, FolderLastModified = sr?.Modified };
            series.Seasons.Add(s);
            seasonRows[number] = s;
            return s;
        }

        var firstOfSeason = new HashSet<int>();
        foreach (var p in placements.OrderBy(p => p.Season == 0).ThenBy(p => p.Season).ThenBy(p => p.Number))
        {
            var season = SeasonFor(p.Season);
            var ep = season.Episodes.FirstOrDefault(e => e.Number == p.Number);
            if (ep == null)
            {
                ep = new Episode { Number = p.Number, EndNumber = p.End, NumberingInferred = p.Inferred };
                season.Episodes.Add(ep);
            }
            var file = new MediaFile
            {
                FileName = p.File.Name, FileUrl = p.File.Url, Extension = Path.GetExtension(p.File.Name).ToLowerInvariant(),
                FileSize = p.File.Size, LastModified = p.File.Modified, FileFingerprint = MediaInspectionService.Fingerprint(p.File.Size, p.File.Modified),
                Episode = ep
            };
            movie.MediaFiles.Add(file);
            ep.MediaFiles.Add(file);
            changedSeasons.Add(p.Season);
            changed = true;

            // ffprobe: the first episode of each season now (bounded); every other file is inspected when it is first played or by the inspection backfill.
            if (probeBudget > 0 && firstOfSeason.Add(p.Season)) { probeBudget--; await SafeInspectAsync(file, ct); }
            ep.Runtime ??= file.Duration > 0 ? (int?)Math.Round(file.Duration!.Value / 60) : null;
        }

        // ---- subtitles beside episodes (matched by file name or by season/episode numbers)
        var allFiles = movie.MediaFiles.Where(f => f.Episode != null || f.EpisodeId != null).ToList();
        foreach (var sub in listed.Subtitles.Where(s => movie.Subtitles.All(x => x.FileUrl != s.Url)))
        {
            var target = MatchSubtitle(sub, allFiles, series);
            if (target == null) continue;
            var subRow = new Subtitle { FileName = sub.Name, FileUrl = sub.Url, Format = Path.GetExtension(sub.Name).TrimStart('.').ToLowerInvariant(), Language = SubtitleConverter.GuessLanguage(sub.Name), Episode = target };
            movie.Subtitles.Add(subRow);
            target.Subtitles.Add(subRow);
            changed = true;
        }
        var liveSubs = listed.Subtitles.Select(s => s.Url).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in movie.Subtitles.Where(s => (s.EpisodeId != null || s.Episode != null) && !liveSubs.Contains(s.FileUrl) && listed.VisitedDirs.Contains(ParentDir(s.FileUrl))).ToList())
        {
            db.Subtitles.Remove(gone); movie.Subtitles.Remove(gone); changed = true;
        }

        // ---- tidy: episodes without files, seasons without episodes
        foreach (var s in series.Seasons.ToList())
        {
            foreach (var e in s.Episodes.Where(e => e.MediaFiles.Count == 0 && e.Id != 0).ToList()) { db.Episodes.Remove(e); s.Episodes.Remove(e); changed = true; }
            if (s.Episodes.Count == 0 && s.Id != 0) { db.Seasons.Remove(s); series.Seasons.Remove(s); changed = true; }
        }

        // season bookkeeping for the next incremental scan
        foreach (var sr in unit.Seasons.Where(x => x.Number.HasValue && relistedSeasons.Contains(x.Number!.Value)))
            if (seasonRows.TryGetValue(sr.Number!.Value, out var row)) { row.FolderLastModified = sr.Modified ?? listed.Newest; row.FolderName ??= sr.Name; row.FolderUrl ??= sr.Url; }
        series.LastActivityAt = new[] { unit.Modified, listed.Newest, unit.Seasons.Max(s => s.Modified) }.Where(d => d.HasValue).DefaultIfEmpty(series.LastActivityAt).Max();
        movie.FolderLastModified = unit.Modified ?? listed.Newest ?? movie.FolderLastModified;
        movie.LastDiscoveredAt = DateTime.UtcNow;

        // ---- TMDB TV metadata
        if (isNew)
        {
            if (tmdbOk.Usable)
            {
                tmdbOk.Observe(await _metadata.MatchAndApplySeriesAsync(db, movie, parsed.Title, parsed.Year, ct));
            }
            else movie.MatchNote = _metadata.IsConfigured ? "TMDB unavailable during scan; will retry." : "TMDB is not configured.";
        }
        else if (changedSeasons.Count > 0 && movie.TmdbId.HasValue && tmdbOk.Usable)
        {
            try { await _metadata.ApplySeasonMetadataAsync(db, movie, changedSeasons, ct); }
            catch (TmdbException ex) { _log.LogWarning("TMDB season refresh for '{Title}' failed: {Message}", movie.Title, ex.Message); }
        }

        if (changed) movie.UpdatedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
            return changed;
        }
        catch (DbUpdateException ex) when (isNew && ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            _log.LogInformation("Series {Folder} was already imported by a parallel scan.", unit.Url);
            return false;
        }
    }

    // ================================================================== episode numbering

    /// <summary>
    /// Gives every new file a season and an episode number. Recognised names (S01E01, 1x05, Ep 05 …) keep their numbers; the season comes from the name,
    /// else from the season folder. Files without a recognisable number are naturally sorted and numbered sequentially (after the highest number
    /// already used in that season). Files whose only "number" is a weak leading digit are treated as unnumbered when those digits repeat.
    /// </summary>
    private static List<Placement> Plan(List<(DirEntry File, int? FolderSeason)> files, bool hasSeasonFolders, Dictionary<int, Season> existing)
    {
        var rows = files.Select(f =>
        {
            var p = EpisodeParser.Parse(Path.GetFileNameWithoutExtension(f.File.Name));
            var season = p.Season ?? f.FolderSeason ?? (hasSeasonFolders ? 0 : 1);
            return (f.File, Season: season, Parsed: p);
        }).ToList();

        var result = new List<Placement>();
        foreach (var g in rows.GroupBy(r => r.Season))
        {
            var items = g.OrderBy(r => r.File.Name, NaturalComparer.Instance).ToList();
            var weak = items.Where(i => i.Parsed.Strength == 1).ToList();
            var weakUnreliable = weak.Count > 1 && weak.Select(i => i.Parsed.Number).Distinct().Count() < weak.Count;

            var used = new HashSet<int>(existing.TryGetValue(g.Key, out var es) ? es.Episodes.Select(e => e.Number) : Enumerable.Empty<int>());
            var numbered = items.Where(i => i.Parsed.Number.HasValue && !(i.Parsed.Strength == 1 && weakUnreliable)).ToList();
            var unnumbered = items.Except(numbered).ToList();

            foreach (var i in numbered)
            {
                result.Add(new Placement(i.File, g.Key, i.Parsed.Number!.Value, i.Parsed.EndNumber, false));
                used.Add(i.Parsed.Number.Value);
            }
            var next = used.Count == 0 ? 1 : used.Max() + 1;
            foreach (var i in unnumbered)
            {
                result.Add(new Placement(i.File, g.Key, next, null, true));
                next++;
            }
        }
        return result;
    }

    private static Episode? MatchSubtitle(DirEntry sub, List<MediaFile> files, Series series)
    {
        var subName = Path.GetFileName(sub.Name);
        // 1) the subtitle's name starts with a video's name (Show.S01E01.mkv ↔ Show.S01E01.en.srt)
        foreach (var f in files.OrderByDescending(f => f.FileName.Length))
        {
            var stem = Path.GetFileNameWithoutExtension(f.FileName);
            if (stem.Length > 0 && subName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return f.Episode ?? series.Seasons.SelectMany(s => s.Episodes).FirstOrDefault(e => e.Id == f.EpisodeId);
        }
        // 2) same season/episode numbers in the name
        var p = EpisodeParser.Parse(Path.GetFileNameWithoutExtension(subName));
        if (p is { Season: { } s, Number: { } n, Strength: 3 })
            return series.Seasons.FirstOrDefault(x => x.Number == s)?.Episodes.FirstOrDefault(e => e.Number == n);
        return null;
    }

    private static string ParentDir(string url) => url[..(url.LastIndexOf('/') + 1)];

    private async Task SafeInspectAsync(MediaFile file, CancellationToken ct)
    {
        try { await _inspect.InspectAsync(file, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { file.InspectionError = ex.Message; _log.LogWarning(ex, "Inspection of {File} failed.", file.FileName); }
    }

    // ================================================================== bookkeeping

    private async Task TouchDiscoveredAsync(List<int> ids, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(ct);
        foreach (var chunk in ids.Chunk(500))
        {
            var part = chunk.ToList();
            await db.Movies.Where(m => part.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.LastDiscoveredAt, now), ct);
        }
    }

    /// <summary>Retries TMDB TV for series that could not be matched earlier (e.g. TMDB was offline), a few per scan.</summary>
    private async Task BackfillMetadataAsync(int sourceId, TmdbHealth tmdbOk, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pending = await db.Movies
            .Include(m => m.Series!).ThenInclude(s => s.Seasons).ThenInclude(s => s.Episodes)
            .Where(m => m.SourceId == sourceId && m.ContentType == CatalogType.Series && m.MatchStatus == MatchStatus.Unmatched
                        && (m.MetadataFetchedAt == null || m.MetadataFetchedAt < cutoff) && m.MatchNote != null && m.MatchNote.StartsWith("TMDB"))
            .OrderBy(m => m.UpdatedAt).Take(15).ToListAsync(ct);

        foreach (var movie in pending)
        {
            if (!tmdbOk.Usable) break;
            var parsed = SeriesTitle.Parse(movie.FolderName);
            var outcome = await _metadata.MatchAndApplySeriesAsync(db, movie, parsed.Title, parsed.Year ?? movie.ReleaseYear, ct);
            tmdbOk.Observe(outcome);
            movie.MetadataFetchedAt = DateTime.UtcNow;
        }
        if (pending.Count > 0) await db.SaveChangesAsync(ct);
    }
}
