using System.Text.RegularExpressions;

namespace StreamApp.Infrastructure.Crawler;

/// <summary>A season folder such as "Season 1", "S02", "Staffel 3" or "Specials", or a titled one such as "Show Name S02".</summary>
public static class SeasonFolder
{
    private static readonly Regex PlainRx = new(
        @"^\s*(?:season|series|saison|temporada|staffel|stagione|sezon|s|part|vol(?:ume)?\.?|book)[\s._\-]*0*(\d{1,3})(?![A-Za-z0-9])(?!\s*[eE]\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpecialsRx = new(@"^\s*(?:specials?|season[\s._\-]*0+|s0+|extras?[\s._\-]*specials?)\s*(?:$|[\s._\-\[\(])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitledRx = new(
        @"^(?<title>.*?\S)[\s._\-\[\(]+(?:season|saison|temporada|staffel|s)[\s._\-]*0*(?<n>\d{1,2})(?![A-Za-z0-9])(?!\s*[eE]\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A folder that IS a season (its name starts with the season marker).</summary>
    public static bool TryParse(string name, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (SpecialsRx.IsMatch(name)) return true;   // number stays 0
        var m = PlainRx.Match(name);
        if (!m.Success) return false;
        number = int.Parse(m.Groups[1].Value);
        return number <= 200;
    }

    /// <summary>A folder named "Show Name S02" / "Show Name Season 2": returns the title part and the season.</summary>
    public static bool TryParseTitled(string name, out string title, out int number)
    {
        title = ""; number = 0;
        if (string.IsNullOrWhiteSpace(name) || TryParse(name, out _)) return false;
        var m = TitledRx.Match(name);
        if (!m.Success) return false;
        title = m.Groups["title"].Value.Trim(' ', '.', '_', '-', '[', '(');
        number = int.Parse(m.Groups["n"].Value);
        return title.Length > 0 && number <= 200;
    }
}

/// <param name="Strength">3 = explicit season+episode marker, 2 = episode marker (Ep 05, " - 05"), 1 = a bare leading number (weak), 0 = nothing found.</param>
public sealed record ParsedEpisode(int? Season, int? Number, int? EndNumber, int Strength = 0);

/// <summary>Extracts season / episode numbers from file names: S01E01, S01E01E02, 1x05, "Season 1 Episode 5", "Ep 05", "[Group] Show - 05", "05 - Title".</summary>
public static class EpisodeParser
{
    private static readonly Regex SxE = new(@"(?<![A-Za-z0-9])S(?<s>\d{1,3})[\s._\-]*E(?<e>\d{1,4})(?<more>(?:[\s._\-]*(?:E|-E?|~E?)\d{1,4})*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MoreNums = new(@"\d{1,4}", RegexOptions.Compiled);
    private static readonly Regex NxM = new(@"(?<![\dA-Za-z])(?<s>\d{1,2})x(?<e>\d{2,3})(?!\d|[pPiI])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Long = new(@"Season[\s._\-]*(?<s>\d{1,3})[\s._\-,]*(?:Episode|Ep)[\s._\-]*(?<e>\d{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpOnly = new(@"(?<![A-Za-z0-9])(?:Episode|Ep|E)[\s._\-]*(?<e>\d{1,4})(?![\dA-Za-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DashNum = new(@"(?:^|[\s\]\)])-[\s]*(?<e>\d{1,4})(?:v\d)?(?=[\s\[\(._]|$)", RegexOptions.Compiled);
    private static readonly Regex LeadNum = new(@"^\s*[\[\(]?(?<e>\d{1,3})[\]\)]?(?:v\d)?(?=[\s._\-]|$)", RegexOptions.Compiled);

    // Things that look like numbers but are not episodes.
    private static readonly Regex NoiseRx = new(@"\b(?:19|20)\d{2}\b|\b\d{3,4}[pPiI]\b|\b\d{3,4}x\d{3,4}\b|\b[xXhH]\.?26[45]\b|\b\d{1,2}bit\b|\b\d\.\d\b|\b5\.1\b|\b7\.1\b|\b2\.0\b", RegexOptions.Compiled);

    public static ParsedEpisode Parse(string fileNameWithoutExtension)
    {
        var name = fileNameWithoutExtension ?? "";

        var m = SxE.Match(name);
        if (m.Success)
        {
            var first = int.Parse(m.Groups["e"].Value);
            int? last = null;
            foreach (Match n in MoreNums.Matches(m.Groups["more"].Value)) last = int.Parse(n.Value);
            if (last is { } l && l <= first) last = null;
            if (last is { } l2 && l2 - first > 30) last = null;   // "S01E01-E99" is not a sensible multi-episode file
            return new ParsedEpisode(int.Parse(m.Groups["s"].Value), first, last, 3);
        }

        m = Long.Match(name);
        if (m.Success) return new ParsedEpisode(int.Parse(m.Groups["s"].Value), int.Parse(m.Groups["e"].Value), null, 3);

        var masked = NoiseRx.Replace(name, " ");
        m = NxM.Match(masked);
        if (m.Success) return new ParsedEpisode(int.Parse(m.Groups["s"].Value), int.Parse(m.Groups["e"].Value), null, 3);

        m = EpOnly.Match(masked);
        if (m.Success) return new ParsedEpisode(null, int.Parse(m.Groups["e"].Value), null, 2);

        m = DashNum.Match(masked);
        if (m.Success) return new ParsedEpisode(null, int.Parse(m.Groups["e"].Value), null, 2);

        m = LeadNum.Match(masked);
        if (m.Success) return new ParsedEpisode(null, int.Parse(m.Groups["e"].Value), null, 1);

        return new ParsedEpisode(null, null, null);
    }

    /// <summary>True when the name carries an explicit season+episode marker (S01E01, 1x05, "Season 1 Episode 5").</summary>
    public static bool HasSeasonEpisodeMarker(string fileNameWithoutExtension)
    {
        var p = Parse(fileNameWithoutExtension);
        return p.Season.HasValue && p.Number.HasValue;
    }
}

/// <summary>Compares names the way a person sorts them: "Episode 2" before "Episode 10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i; var sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length < nb.Length ? -1 : 1;
                var c = na.CompareTo(nb, StringComparison.Ordinal);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

/// <summary>Turns a series folder name into a title for TMDB: drops season ranges, "Complete" and similar release noise.</summary>
public static class SeriesTitle
{
    private static readonly Regex TailRx = new(
        @"[\s._\-\[\(]+(?:S\d{1,2}(?:[\s._\-]*(?:-|to)?[\s._\-]*S?\d{1,2})?|Seasons?[\s._\-]*\d{1,2}(?:[\s._\-]*(?:-|to|&)[\s._\-]*\d{1,2})?|Complete(?:[\s._\-]*Series)?|Collection)(?![A-Za-z]).*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedName Parse(string folderName)
    {
        var cleaned = TailRx.Replace(folderName ?? "", "");
        if (cleaned.Trim().Length == 0) cleaned = folderName ?? "";
        return FolderNameParser.Parse(cleaned);
    }
}
