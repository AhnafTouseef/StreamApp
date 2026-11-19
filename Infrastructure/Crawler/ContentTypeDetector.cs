using System.Text.RegularExpressions;
using StreamApp.Models;

namespace StreamApp.Infrastructure.Crawler;

public sealed record ContentTypeDetection(CatalogType Type, string Reason);

/// <summary>
/// Decides from the directory structure alone whether a source holds movies or TV series (never from category names).
/// A small, evenly spread sample of folders is listed; season folders that contain video files and episode-numbered file names
/// (S01E01 …) are series evidence, a folder with a non-episodic video file is movie evidence. Alphabetical or other grouping folders
/// are looked into one more level, so Root → Series → Season → Episodes and Root → Group → Series → Season → Episodes both work.
/// With no evidence at all the answer is Movie, which keeps every existing source behaving as before.
/// </summary>
public sealed class ContentTypeDetector
{
    private const int MaxListings = 24;
    private const int RootSample = 8;
    private const int GroupSample = 3;
    private static readonly Regex IgnoreDirRx = new(@"^(sample|samples|extras?|featurettes?|trailers?|screens?|screenshots?|behind the scenes|subs?|subtitles?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SampleFileRx = new(@"(^|[\s._\-\[\(])sample([\s._\-\]\)]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DirectoryListingClient _listing;
    private readonly MediaKinds _kinds;

    public ContentTypeDetector(DirectoryListingClient listing, MediaKinds kinds) { _listing = listing; _kinds = kinds; }

    private enum Kind { Series, Movie, Group, Empty }

    public async Task<ContentTypeDetection> DetectAsync(IReadOnlyList<DirEntry> rootEntries, FolderFilter filter, CancellationToken ct)
    {
        var listings = 0;
        int series = 0, movie = 0;
        var seasonProofs = 0;

        // Loose episode files directly in the root: the root itself is a series folder.
        var rootVideos = rootEntries.Where(e => !e.IsDirectory && _kinds.IsVideo(e.Name) && !SampleFileRx.IsMatch(e.Name)).ToList();
        if (rootVideos.Count(v => EpisodeParser.HasSeasonEpisodeMarker(Path.GetFileNameWithoutExtension(v.Name))) >= 2) series += 3;
        else if (rootVideos.Count > 0) movie += 1;

        var rootDirs = rootEntries.Where(e => e.IsDirectory && !filter.IsExcluded(e.Name) && !IgnoreDirRx.IsMatch(e.Name) && filter.IsIncluded(e.Name)).ToList();

        // The root folder itself holds season folders.
        if (rootDirs.Count(d => SeasonFolder.TryParse(d.Name, out _)) >= 1)
        {
            series += 3;
            if (await SeasonHasVideosAsync(rootDirs.First(d => SeasonFolder.TryParse(d.Name, out _)), ct)) { series += 2; seasonProofs++; listings++; }
        }

        var reasons = new List<string>();
        foreach (var dir in Spread(rootDirs, RootSample))
        {
            ct.ThrowIfCancellationRequested();
            if (listings >= MaxListings) break;
            var (kind, entries) = await ClassifyAsync(dir, ct); listings++;
            if (kind == Kind.Group)
            {
                foreach (var child in Spread(entries.Where(e => e.IsDirectory && !filter.IsExcluded(e.Name) && !IgnoreDirRx.IsMatch(e.Name)).ToList(), GroupSample))
                {
                    if (listings >= MaxListings) break;
                    var (ck, centries) = await ClassifyAsync(child, ct); listings++;
                    Vote(ck, centries);
                    if (ck == Kind.Series && seasonProofs < 3 && listings < MaxListings && await ConfirmSeasonAsync(centries, ct)) { listings++; seasonProofs++; series += 1; }
                }
            }
            else
            {
                Vote(kind, entries);
                if (kind == Kind.Series && seasonProofs < 3 && listings < MaxListings && await ConfirmSeasonAsync(entries, ct)) { listings++; seasonProofs++; series += 1; }
            }
        }

        void Vote(Kind k, IReadOnlyList<DirEntry> _)
        {
            if (k == Kind.Series) series += 2;
            else if (k == Kind.Movie) movie += 1;
        }

        if (series > movie && series > 0)
            return new ContentTypeDetection(CatalogType.Series, $"season folders / episode-numbered files found in the structure (series evidence {series}, movie evidence {movie}).");
        if (movie > 0)
            return new ContentTypeDetection(CatalogType.Movie, $"folders with plain movie files found (movie evidence {movie}, series evidence {series}).");
        return new ContentTypeDetection(CatalogType.Movie, "no series structure found.");
    }

    private async Task<(Kind Kind, IReadOnlyList<DirEntry> Entries)> ClassifyAsync(DirEntry dir, CancellationToken ct)
    {
        IReadOnlyList<DirEntry> entries;
        try { entries = await _listing.ListAsync(dir.Url, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (Kind.Empty, Array.Empty<DirEntry>());
        }
        return (Classify(entries), entries);
    }

    private Kind Classify(IReadOnlyList<DirEntry> entries)
    {
        var dirs = entries.Where(e => e.IsDirectory && !IgnoreDirRx.IsMatch(e.Name)).ToList();
        var videos = entries.Where(e => !e.IsDirectory && _kinds.IsVideo(e.Name) && !SampleFileRx.IsMatch(e.Name)).ToList();
        var marked = videos.Count(v => EpisodeParser.HasSeasonEpisodeMarker(Path.GetFileNameWithoutExtension(v.Name)));

        if (dirs.Any(d => SeasonFolder.TryParse(d.Name, out _))) return Kind.Series;
        if (marked >= 2 || (videos.Count >= 2 && marked == videos.Count)) return Kind.Series;
        if (videos.Count > 0) return Kind.Movie;
        if (dirs.Count > 0) return Kind.Group;
        return Kind.Empty;
    }

    private async Task<bool> ConfirmSeasonAsync(IReadOnlyList<DirEntry> seriesEntries, CancellationToken ct)
    {
        var season = seriesEntries.FirstOrDefault(e => e.IsDirectory && SeasonFolder.TryParse(e.Name, out _));
        return season != null && await SeasonHasVideosAsync(season, ct);
    }

    private async Task<bool> SeasonHasVideosAsync(DirEntry seasonDir, CancellationToken ct)
    {
        try
        {
            var entries = await _listing.ListAsync(seasonDir.Url, ct);
            if (entries.Any(e => !e.IsDirectory && _kinds.IsVideo(e.Name) && !SampleFileRx.IsMatch(e.Name))) return true;
            // episodes in their own sub-folders
            var sub = entries.FirstOrDefault(e => e.IsDirectory && !IgnoreDirRx.IsMatch(e.Name));
            if (sub == null) return false;
            var inner = await _listing.ListAsync(sub.Url, ct);
            return inner.Any(e => !e.IsDirectory && _kinds.IsVideo(e.Name));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    /// <summary>Up to <paramref name="max"/> items spread evenly over the (sorted) list, so the sample is not just the first letters.</summary>
    private static List<DirEntry> Spread(List<DirEntry> dirs, int max)
    {
        var sorted = dirs.OrderBy(d => d.Name, NaturalComparer.Instance).ToList();
        if (sorted.Count <= max) return sorted;
        var picked = new List<DirEntry>(max);
        for (var i = 0; i < max; i++) picked.Add(sorted[(int)((long)i * sorted.Count / max)]);
        return picked;
    }
}
