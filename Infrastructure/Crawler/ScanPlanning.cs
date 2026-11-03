using System.Text.RegularExpressions;
using MovieHub.Models;

namespace MovieHub.Infrastructure.Crawler;

/// <summary>One movie to ingest: either a folder (the usual case) or a loose video file.</summary>
public sealed record MovieUnit(string Name, string Url, bool IsFolder, DateTime? Modified, long? Size);

/// <summary>Recognises year folders such as "(1995) 1080p &amp; Before", "2000 &amp; Before", "(2009) &amp; Before" or "2024".</summary>
public static class YearFolder
{
    private static readonly Regex Rx = new(@"^\s*\(?\s*((?:19|20)\d{2})\s*\)?(?=$|[\s&\-_\[\(])", RegexOptions.Compiled);

    public static bool TryParse(string name, out int year)
    {
        year = 0;
        var m = Rx.Match(name);
        if (!m.Success) return false;
        // "2012 (2009) 1080p" is a movie folder that happens to start with a year-like title, not a year folder.
        if (OtherYear.IsMatch(name[m.Length..])) return false;
        year = int.Parse(m.Groups[1].Value);
        return year <= DateTime.UtcNow.Year + 1;
    }

    private static readonly Regex OtherYear = new(@"(?<!\d)(?:19|20)\d{2}(?!\d)", RegexOptions.Compiled);
}

public static class YearPlanner
{
    /// <summary>
    /// Decides which year folders to list.
    /// Routine scan: the latest year (always re-checked for newly added movies) plus any year newer than the newest one
    /// already recorded (a freshly appeared year folder; the previous latest year is checked once more then); older years are skipped.
    /// Deep scan, or the very first scan of a source (nothing recorded yet, so this is the initial import): every year,
    /// newest first so recent movies show up in the catalog early.
    /// </summary>
    public static IReadOnlyList<(string Url, int Year)> Choose(
        IReadOnlyCollection<(string Url, int Year)> yearDirs, IReadOnlyCollection<int> knownYears, bool deep)
    {
        if (yearDirs.Count == 0) return Array.Empty<(string, int)>();
        if (deep || knownYears.Count == 0) return yearDirs.OrderByDescending(d => d.Year).ToList();

        var latest = yearDirs.Max(d => d.Year);
        var maxKnown = knownYears.Max();
        // When a new year folder appears, the previous latest year gets one last check: it was "the latest" until now and may still receive late additions.
        return yearDirs.Where(d => d.Year == latest || d.Year > maxKnown || (latest > maxKnown && d.Year == maxKnown))
            .OrderByDescending(d => d.Year).ToList();
    }
}

/// <summary>Administrator-configured include/exclude lists (one pattern per line; * and ? wildcards; case-insensitive).</summary>
public sealed class FolderFilter
{
    private readonly List<Regex> _include;
    private readonly List<Regex> _exclude;

    public FolderFilter(string? included, string? excluded)
    {
        _include = Compile(included);
        _exclude = Compile(excluded);
    }

    public bool HasIncludes => _include.Count > 0;
    public bool IsExcluded(string name) => _exclude.Any(r => r.IsMatch(name));
    public bool IsIncluded(string name) => _include.Count == 0 || _include.Any(r => r.IsMatch(name));

    private static List<Regex> Compile(string? text) =>
        (text ?? "")
            .Split(new[] { '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToList();
}

public sealed record StrategySuggestion(ScanStrategy Strategy, string Reason, int DirectoryCount, int YearDirectoryCount, int VideoFileCount);

public static class StrategyDetector
{
    /// <summary>Looks at a root listing and proposes a scan strategy: year folders, flat movie folders, or recursive.</summary>
    public static StrategySuggestion Suggest(IReadOnlyCollection<DirEntry> rootEntries, MediaKinds kinds)
    {
        var dirs = rootEntries.Where(e => e.IsDirectory).ToList();
        var yearDirs = dirs.Count(d => YearFolder.TryParse(d.Name, out _));
        var videos = rootEntries.Count(e => !e.IsDirectory && kinds.IsVideo(e.Name));

        if (dirs.Count >= 3 && yearDirs >= 3 && yearDirs >= dirs.Count / 2.0)
            return new StrategySuggestion(ScanStrategy.YearBased,
                $"{yearDirs} of {dirs.Count} top-level folders look like year folders.", dirs.Count, yearDirs, videos);

        if (dirs.Count == 0 && videos == 0)
            return new StrategySuggestion(ScanStrategy.Flat, "The root listing is empty.", 0, 0, 0);

        return new StrategySuggestion(ScanStrategy.Flat,
            $"{dirs.Count} top-level folders ({yearDirs} year-like): each folder is treated as one movie.", dirs.Count, yearDirs, videos);
    }
}

public static class UnitBuilder
{
    /// <summary>Turns a directory listing into movie units: sub-folders and loose video files.</summary>
    public static List<MovieUnit> FromEntries(IEnumerable<DirEntry> entries, FolderFilter filter, MediaKinds kinds, bool applyIncludeFilter)
    {
        var units = new List<MovieUnit>();
        foreach (var e in entries)
        {
            if (e.IsDirectory)
            {
                if (filter.IsExcluded(e.Name)) continue;
                if (applyIncludeFilter && !filter.IsIncluded(e.Name)) continue;
                units.Add(new MovieUnit(e.Name, e.Url, true, e.Modified, null));
            }
            else if (kinds.IsVideo(e.Name))
            {
                units.Add(new MovieUnit(e.Name, e.Url, false, e.Modified, e.Size));
            }
        }
        return units;
    }
}

public static class ScanDecision
{
    /// <summary>
    /// Decides whether a visit should trigger a scan of this source. Disabled and manual-only sources never auto-scan;
    /// interrupted scans resume immediately; failed scans back off; otherwise the per-source cooldown applies.
    /// </summary>
    public static bool IsDue(Source s, DateTime nowUtc, int failureBackoffMinutes)
    {
        if (!s.IsEnabled || s.ScanStrategy == ScanStrategy.ManualFull) return false;
        if (s.LastScanStatus == ScanRunStatus.Running) return false;
        if (s.LastScanStatus == ScanRunStatus.Interrupted) return true;

        if (s.LastScanStatus == ScanRunStatus.Failed && s.LastScanAttempt is { } attempt)
            return nowUtc - attempt >= TimeSpan.FromMinutes(failureBackoffMinutes);

        if (s.LastSuccessfulScan is null) return true;
        return nowUtc - s.LastSuccessfulScan.Value >= TimeSpan.FromHours(Math.Max(1, s.CooldownHours));
    }
}
