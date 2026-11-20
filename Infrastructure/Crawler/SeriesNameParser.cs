using System.Text.RegularExpressions;

namespace StreamApp.Infrastructure.Crawler;

/// <summary>
/// What a series directory name says. The original directory name is never changed; everything extracted from it lives here (and is stored
/// separately on the catalog entry): a clean <see cref="Title"/>, a <see cref="Year"/> that is only a clue, and the descriptive
/// <see cref="Labels"/> (classification, quality, audio, season range …).
/// </summary>
/// <param name="Queries">Title variants for TMDB, best first (e.g. with and without a parenthesised part of the title).</param>
/// <param name="Region">A two-letter country qualifier such as "(US)" / "(UK)", used to tell same-named shows apart.</param>
/// <param name="TmdbId">An id written into the folder name by a provider or a media manager ("{tmdb-1234}", "[tmdbid-1234]").</param>
public sealed record SeriesName(string Title, int? Year, int? EndYear, string? Quality, string? Region, int? TmdbId,
    IReadOnlyList<string> Queries, IReadOnlyList<string> Labels, string Original)
{
    public string Tags => string.Join(", ", Labels);
}

/// <summary>
/// Reads series directory names. Unlike movie names these rarely follow one convention, so nothing is stripped blindly:
/// a bracketed part is judged by its content - a year, a quality/audio/season/rating label or a country code is metadata, anything else
/// ("(Thunderbolt)", "[Brotherhood]", "(Special Edition)") may be part of the title and is kept in the primary search title,
/// while the title without it is searched as a fallback.
/// </summary>
public static class SeriesNameParser
{
    private static readonly Regex GroupRx = new(@"\(([^()]*)\)|\[([^\[\]]*)\]|\{([^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex IdRx = new(@"[\[\{\(]\s*(?<k>tmdb(?:id)?|imdb(?:id)?|tvdb(?:id)?)\s*[-=:_ ]\s*(?<v>(?:tt)?\d+)\s*[\]\}\)]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeadGroupRx = new(@"^\s*\[[^\]]{1,40}\]\s*(?=\S)", RegexOptions.Compiled);
    private static readonly Regex SiteRx = new(@"^\s*(?:www\.)?[\w-]+\.(?:com|net|org|info|me|io|cc|to|tv|xyz|vip|club)\s*[-–—|:]+\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpacesRx = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex YearRx = new(@"(?<!\d)((?:19|20)\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex RangeRx = new(@"(?<!\d)((?:19|20)\d{2})\s*[-–—]\s*((?:19|20)\d{2})?(?!\d)", RegexOptions.Compiled);
    private static readonly Regex QualityRx = new(@"\b(2160p|4k|1440p|1080p|720p|576p|480p|360p)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RatingRx = new(@"^(?:tv[\s-]?(?:y7?|g|pg|14|ma)|pg(?:-?13)?|nc-?17|18\+|16\+|13\+|12\+|nr)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RegionRx = new(@"^[A-Z]{2}$", RegexOptions.Compiled);

    // A word that, inside a bracket, says "this is release/provider information, not part of the title".
    private static readonly Regex NoiseWordRx = new(
        @"^(?:\d{3,4}[pi]|[48]k|uhd|fhd|hd|sd|qhd|blu-?ray|bd|bd-?rip|br-?rip|web-?dl|web-?rip|web|hdtv|hd-?rip|dvd|dvd-?rip|dvd-?scr|hd-?cam|cam|remux|amzn|nf|netflix|dsnp|hmax|atvp|hulu|pcok|crunchyroll|cr|funi|disney\+?|max|itunes|"
        + @"x26[45]|h\.?26[45]|hevc|avc|av1|xvid|divx|10-?bit|8-?bit|hdr(?:10)?\+?|dv|dolby|vision|aac(?:[25]\.[01])?|ac-?3|e-?ac-?3|ddp?\+?(?:[25]\.[01])?|dts(?:-?hd)?|truehd|atmos|flac|mp3|opus|[257]\.[01]|"
        + @"dual|multi|audio|dubbed|dub|dubs|sub|subs|subbed|e-?subs?|hindi|english|eng|korean|kor|japanese|jap|jpn|chinese|mandarin|cantonese|tamil|telugu|malayalam|kannada|bengali|bangla|urdu|punjabi|marathi|thai|turkish|arabic|french|german|spanish|italian|russian|portuguese|indonesian|vietnamese|hin|tam|tel|ben|chi|org|original|"
        + @"complete|completed|ongoing|full|series|show|tv|anime|cartoon|season|seasons|collection|mini|miniseries|episodes?|eps?|batch|uncut|uncensored|extended|unrated|proper|repack|internal|remastered|ntsc|pal|hq|new|updated|final|all|en|hi|ko|ja|zh|ta|te|ml|kn|bn|"
        + @"s\d{1,2}|e\d{1,3}|\d{1,2}|to|and|with|\+|&|-|–|—)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Plain text (outside brackets) where the title certainly ends.
    private static readonly Regex PlainBoundaryRx = new(
        @"(?<![A-Za-z0-9])(?:S\d{1,2}(?:[\s._\-]*(?:-|to)?[\s._\-]*S?\d{1,2})?(?![A-Za-z0-9])|Seasons?[\s._\-]*\d{1,2}|Complete(?![A-Za-z])|Collection(?![A-Za-z])|Mini[\s._\-]*Series|TV[\s._\-]*(?:Series|Show)|(?:2160|1440|1080|720|576|480|360)[pP]|4[kK]|"
        + @"Blu-?Ray|BD-?Rip|BR-?Rip|WEB-?DL|WEB-?Rip|HDTV|HD-?Rip|DVD-?Rip|HDCAM|REMUX|[xX]26[45]|[hH]\.?26[45]|HEVC|(?:Dual|Multi)[\s._\-]*Audio|Dubbed|E-?Subs?|10-?bit)",
        RegexOptions.Compiled);

    public static SeriesName Parse(string? folderName)
    {
        var original = folderName ?? "";
        var s = original.Trim();
        var labels = new List<string>();
        int? tmdbId = null;

        // provider / media-manager ids: "{tmdb-1234}", "[tmdbid-1234]", "[imdbid-tt0903747]"
        foreach (Match m in IdRx.Matches(s))
            if (m.Groups["k"].Value.StartsWith("tmdb", StringComparison.OrdinalIgnoreCase) && int.TryParse(m.Groups["v"].Value, out var id)) tmdbId = id;
        s = IdRx.Replace(s, " ");

        s = SiteRx.Replace(s, "");
        if (LeadGroupRx.IsMatch(s) && GroupRx.Replace(s, "").Trim().Length > 0)          // "[SubsPlease] Title …" - a release group, not a title
        {
            var m = LeadGroupRx.Match(s);
            AddLabel(labels, m.Value.Trim().Trim('[', ']', ' '));
            s = s[m.Length..];
        }

        // a serial number an uploader put in front to rank the list ("001. Show", "#7 Show", "(03) Show"): not part of the name
        var unranked = FolderNameParser.StripStrongRank(s);
        if (unranked.Length > 0 && unranked.Length != s.Trim().Length)
        {
            AddLabel(labels, "Rank: " + s.Trim()[..(s.Trim().Length - unranked.Length)].Trim(' ', '#', '.', ')', '(', '[', ']', ':', '_', '-', '–', '—'));
            s = unranked;
        }

        // "Show.Name.2019.S01" / "Show_Name": separators stand for spaces, but dots are kept when the name already has spaces.
        if (!s.Contains(' ')) s = s.Replace('.', ' ');
        s = s.Replace('_', ' ');

        int? year = null, endYear = null;
        string? region = null;
        var cut = int.MaxValue;

        // ---- bracketed parts: judge each one by what it says
        var groupSpans = new List<(int Index, int Length, bool Keep)>();
        foreach (Match g in GroupRx.Matches(s))
        {
            var content = (g.Groups[1].Success ? g.Groups[1].Value : g.Groups[2].Success ? g.Groups[2].Value : g.Groups[3].Value).Trim();
            var kind = Classify(content, out var y, out var ey, out var label);
            switch (kind)
            {
                case Kind.Year: year ??= y; endYear ??= ey; AddLabel(labels, label); break;
                case Kind.Region: region ??= content; AddLabel(labels, "Region: " + content); break;
                case Kind.Rating: AddLabel(labels, content.ToUpperInvariant()); break;
                case Kind.Noise: AddLabel(labels, label); break;
            }
            var keep = kind == Kind.Title;
            groupSpans.Add((g.Index, g.Length, keep));
            if (!keep && g.Index < cut) cut = g.Index;
        }

        // ---- plain text with the brackets blanked out
        var masked = GroupRx.Replace(s, m => new string(' ', m.Length));
        var pb = PlainBoundaryRx.Match(masked);
        if (pb.Success && pb.Index > 0 && pb.Index < cut) cut = pb.Index;

        // a bare year ends the title only when it is the last word or is followed by release information (so "Blade Runner 2049" stays whole)
        foreach (Match ym in YearRx.Matches(masked))
        {
            if (ym.Index == 0 || ym.Index >= cut || int.Parse(ym.Groups[1].Value) > DateTime.UtcNow.Year + 1) continue;
            var rest = masked[(ym.Index + ym.Length)..];
            if (Regex.IsMatch(rest, @"^\s*(?:[-–—]\s*(?:(?:19|20)\d{2})?\s*)?(?:$|S\d|Season|Complete|\d{3,4}[pP]|WEB|Blu|HDTV|BD|DVD|[xXhH]\.?26)", RegexOptions.IgnoreCase))
            {
                year ??= int.Parse(ym.Groups[1].Value);
                var rm = RangeRx.Match(masked, ym.Index);
                if (rm.Success && rm.Index == ym.Index && rm.Groups[2].Success) endYear ??= int.Parse(rm.Groups[2].Value);
                cut = ym.Index;
                break;
            }
        }
        // "Show Name 2019-2023" without brackets is covered above; "Show Name (2019-)" by Classify.

        // ---- the title, with title-like brackets kept
        string titleRaw;
        if (cut == int.MaxValue) titleRaw = s;
        else if (cut > 0) titleRaw = s[..cut];
        else titleRaw = GroupRx.Replace(s, m => groupSpans.FirstOrDefault(x => x.Index == m.Index).Keep ? m.Value : " ");   // metadata first: keep what remains
        var tail = cut is > 0 and < int.MaxValue ? s[cut..] : "";

        // labels from the plain tail ("... 1080p WEB-DL Complete")
        foreach (var piece in PlainLabels(GroupRx.Replace(tail, " "))) AddLabel(labels, piece);
        var quality = QualityRx.Match(original) is { Success: true } qm ? qm.Value.ToLowerInvariant() : null;
        if (quality != null) AddLabel(labels, quality);

        var title = CleanTitle(titleRaw);
        if (title.Length == 0) title = CleanTitle(GroupRx.Replace(s, " "));
        if (title.Length == 0) title = SpacesRx.Replace(original.Replace('_', ' '), " ").Trim();

        return new SeriesName(title, year, endYear, quality, region, tmdbId, Variants(title), labels.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), original);
    }

    // ------------------------------------------------------------------ helpers

    private enum Kind { Title, Year, Region, Rating, Noise }

    private static Kind Classify(string content, out int? year, out int? endYear, out string label)
    {
        year = null; endYear = null; label = content;
        if (content.Length == 0) return Kind.Noise;

        var rm = RangeRx.Match(content);
        var ym = YearRx.Match(content);
        if (ym.Success)
        {
            year = int.Parse(ym.Groups[1].Value);
            if (rm.Success && rm.Groups[2].Success) endYear = int.Parse(rm.Groups[2].Value);
            var rest = YearRx.Replace(content, " ");
            rest = Regex.Replace(rest, @"[-–—]", " ");
            if (WordsAreNoise(rest)) { label = SpacesRx.Replace(content, " ").Trim(); return Kind.Year; }
            // a year next to real words, e.g. "(Doctor Who 2005)", is part of the title
            year = null; endYear = null;
            return Kind.Title;
        }

        if (RatingRx.IsMatch(content.Trim())) return Kind.Rating;
        if (WordsAreNoise(content)) return Kind.Noise;
        if (RegionRx.IsMatch(content)) return Kind.Region;
        return Kind.Title;
    }

    /// <summary>True when every word is release/provider vocabulary (an empty string counts: nothing but punctuation is left).</summary>
    private static bool WordsAreNoise(string text)
    {
        var words = Regex.Split(text, @"[\s,;/|:~]+").Where(w => w.Trim('.', '-', '+').Length > 0 || w == "+" ).ToList();
        if (words.Count == 0) return true;
        foreach (var raw in words)
        {
            var w = raw.Trim('.', ',');
            if (NoiseWordRx.IsMatch(w)) continue;
            var parts = w.Split('-', StringSplitOptions.RemoveEmptyEntries);          // "Hin-Eng", "S01-S05", "720p-1080p"
            if (parts.Length > 1 && parts.All(p => NoiseWordRx.IsMatch(p))) continue;
            if (RatingRx.IsMatch(w)) continue;
            return false;
        }
        return true;
    }

    private static IEnumerable<string> PlainLabels(string tail)
    {
        var words = Regex.Split(tail, @"[\s,;/|:~]+").Select(w => w.Trim('.', ',', '-')).Where(w => w.Length > 0).ToList();
        var run = new List<string>();
        foreach (var w in words)
        {
            var parts = w.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if ((NoiseWordRx.IsMatch(w) || parts.Length > 1 && parts.All(x => NoiseWordRx.IsMatch(x))) && !(w.Length <= 2 && !w.Any(char.IsLetter))) { run.Add(w); continue; }
            if (run.Count > 0) { yield return string.Join(" ", run); run.Clear(); }
        }
        if (run.Count > 0) yield return string.Join(" ", run);
    }

    private static void AddLabel(List<string> labels, string? label)
    {
        label = SpacesRx.Replace((label ?? "").Trim(), " ");
        if (label.Length > 0 && label.Length <= 80) labels.Add(label);
    }

    private static string CleanTitle(string raw)
    {
        var t = SpacesRx.Replace(raw, " ").Trim(' ', '-', '–', '—', ',', ':', '|');
        // brackets are trimmed only when unbalanced, so "Gundam (Thunderbolt)" keeps its closing bracket
        t = t.Trim();
        while (t.Length > 0 && (t[0] is ')' or ']' or '}' || t[^1] is '(' or '[' or '{')) t = t[0] is ')' or ']' or '}' ? t[1..].TrimStart() : t[..^1].TrimEnd();
        while (t.Length > 0 && (t[0] is '(' or '[' or '{') && !t.Contains(t[0] == '(' ? ')' : t[0] == '[' ? ']' : '}')) t = t[1..].TrimStart();
        while (t.Length > 0 && t[^1] is ')' or ']' or '}' && !t.Contains(t[^1] == ')' ? '(' : t[^1] == ']' ? '[' : '{')) t = t[..^1].TrimEnd();
        // a trailing dot belongs to the title in abbreviations such as "S.W.A.T."
        if (t.EndsWith('.') && !Regex.IsMatch(t, @"(?:^|[\s.])[A-Za-z](?:\.[A-Za-z])+\.$")) t = t.TrimEnd('.', ' ');
        return t.TrimStart(' ', '.').TrimEnd();
    }

    /// <summary>Search titles, best first: the title as written, then without bracketed parts, then each side of "A - B", "A / B" and "A aka B".</summary>
    private static IReadOnlyList<string> Variants(string title)
    {
        var list = new List<string> { title };
        if (FolderNameParser.StripWeakRank(title) is { Length: > 1 } unranked) list.Add(unranked);   // "12 - The Office": maybe a rank, so also searched without it
        var noParens = CleanTitle(GroupRx.Replace(title, " "));
        if (noParens.Length > 0) list.Add(noParens);
        foreach (var basis in new[] { title, noParens }.Distinct())
        {
            var parts = Regex.Split(basis, @"\s+(?:-|–|—|/|\|)\s+|\s+aka\s+", RegexOptions.IgnoreCase).Select(CleanTitle).Where(p => p.Length >= 2).ToList();
            if (parts.Count > 1) list.AddRange(parts);
        }
        var seen = new HashSet<string>();
        return list.Where(t => seen.Add(FolderNameParser.Normalize(t))).ToList();
    }
}
