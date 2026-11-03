using System.Text.RegularExpressions;

namespace MovieHub.Infrastructure.Crawler;

public sealed record ParsedName(string Title, int? Year, string? Quality, string Tags);

/// <summary>Extracts a clean title, release year, quality and tags from names like "5 Centimeters Per Second (2007) 1080p [Multi Audio]".</summary>
public static class FolderNameParser
{
    private static readonly Regex YearBracketed = new(@"[\(\[]\s*((?:19|20)\d{2})\s*[\)\]]", RegexOptions.Compiled);
    private static readonly Regex YearLoose = new(@"(?<!\d)((?:19|20)\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex QualityRx = new(@"\b(2160p|4k|1440p|1080p|720p|576p|480p|360p)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoiseRx = new(@"\b(blu-?ray|bdrip|brrip|web-?dl|webrip|hdrip|hdtv|dvdrip|x264|x265|h\.?264|h\.?265|hevc|aac|ac3|dts|remux|imax)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BracketsRx = new(@"[\[\(]([^\]\)]*)[\]\)]", RegexOptions.Compiled);
    private static readonly Regex SpacesRx = new(@"\s+", RegexOptions.Compiled);

    /// <param name="rawName">Folder name, or file name without its extension.</param>
    public static ParsedName Parse(string rawName)
    {
        var name = (rawName ?? "").Trim();

        int? year = null;
        var cut = -1;

        var bracketed = YearBracketed.Matches(name);
        if (bracketed.Count > 0)
        {
            var m = bracketed[^1];
            year = int.Parse(m.Groups[1].Value);
            cut = m.Index;
        }
        else
        {
            // A bare year counts only when it is not the very first token (so "2012" or "1917" alone stay titles).
            var loose = YearLoose.Matches(name).Where(x => x.Index > 0).ToList();
            if (loose.Count > 0)
            {
                var m = loose[^1];
                year = int.Parse(m.Groups[1].Value);
                cut = m.Index;
            }
        }

        var titlePart = cut > 0 ? name[..cut] : name;

        if (cut <= 0)
        {
            // No usable year: cut at the first quality token, release noise or bracket.
            var candidates = new List<int>();
            var q = QualityRx.Match(name);
            if (q.Success && q.Index > 0) candidates.Add(q.Index);
            var n = NoiseRx.Match(name);
            if (n.Success && n.Index > 0) candidates.Add(n.Index);
            var b = name.IndexOfAny(new[] { '[', '(' });
            if (b > 0) candidates.Add(b);
            if (candidates.Count > 0) titlePart = name[..candidates.Min()];
        }

        if (!titlePart.Contains(' ')) titlePart = titlePart.Replace('.', ' ');
        var title = SpacesRx.Replace(titlePart.Replace('_', ' '), " ").Trim(' ', '-', '.', ',');
        if (title.Length == 0) title = SpacesRx.Replace(name.Replace('_', ' '), " ").Trim();

        var quality = QualityRx.Match(name) is { Success: true } qm ? qm.Value.ToLowerInvariant() : null;

        var tags = BracketsRx.Matches(name)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(t => t.Length > 0 && !Regex.IsMatch(t, @"^(?:19|20)\d{2}$") && !QualityRx.IsMatch(t))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return new ParsedName(title, year, quality, string.Join(", ", tags));
    }

    /// <summary>Lower-cased, accent-free, punctuation-free form used when comparing titles.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var decomposed = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (ch == '&') { sb.Append(" and "); continue; }
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return SpacesRx.Replace(sb.ToString(), " ").Trim();
    }
}
