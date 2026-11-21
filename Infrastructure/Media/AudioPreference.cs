using System.Text.RegularExpressions;

namespace StreamApp.Infrastructure.Media;

/// <summary>
/// Chooses the audio track a movie should start with: Japanese when the file has one, otherwise English, otherwise the file's own default, otherwise the first track.
/// </summary>
public static class AudioPreference
{
    public static bool IsEnglish(string? language, string? title)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            var l = language.Trim();
            if (l.Equals("English", StringComparison.OrdinalIgnoreCase) || l.Equals("eng", StringComparison.OrdinalIgnoreCase) ||
                l.Equals("en", StringComparison.OrdinalIgnoreCase) || l.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Many releases leave the language tag empty/"und" and only name the track ("English 5.1", "ENG AC3").
        if (!string.IsNullOrWhiteSpace(title))
        {
            var t = title.ToLowerInvariant();
            if (Regex.IsMatch(t, @"(?<![a-z])(english|eng)(?![a-z])")) return true;
        }
        return false;
    }

    public static bool IsJapanese(string? language, string? title)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            var l = language.Trim();
            if (l.Equals("Japanese", StringComparison.OrdinalIgnoreCase) || l.Equals("jpn", StringComparison.OrdinalIgnoreCase) ||
                l.Equals("ja", StringComparison.OrdinalIgnoreCase) || l.Equals("jp", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("ja-", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            var t = title.ToLowerInvariant();
            if (Regex.IsMatch(t, @"(?<![a-z])(japanese|jpn)(?![a-z])")) return true;
        }
        return false;
    }

    /// <summary>
    /// Index (in <paramref name="tracks"/>) of the track to start with: the first Japanese one, else the first English one, else the first flagged default, else the first track.
    /// -1 when there are no tracks.
    /// </summary>
    public static int Choose<T>(IReadOnlyList<T> tracks, Func<T, string?> language, Func<T, string?> title, Func<T, bool> isDefault)
    {
        if (tracks.Count == 0) return -1;
        for (var i = 0; i < tracks.Count; i++)
            if (IsJapanese(language(tracks[i]), title(tracks[i]))) return i;
        for (var i = 0; i < tracks.Count; i++)
            if (IsEnglish(language(tracks[i]), title(tracks[i]))) return i;
        for (var i = 0; i < tracks.Count; i++)
            if (isDefault(tracks[i])) return i;
        return 0;
    }
}
