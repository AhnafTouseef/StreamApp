namespace MovieHub.Infrastructure.Media;

/// <summary>Chooses the audio track a movie should start with: English when the file has one, otherwise the file's own default.</summary>
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
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"(?<![a-z])(english|eng)(?![a-z])")) return true;
        }
        return false;
    }

    /// <summary>
    /// Index (in <paramref name="tracks"/>) of the track to start with: the first English one, else the first flagged default, else the first track.
    /// -1 when there are no tracks.
    /// </summary>
    public static int Choose<T>(IReadOnlyList<T> tracks, Func<T, string?> language, Func<T, string?> title, Func<T, bool> isDefault)
    {
        if (tracks.Count == 0) return -1;
        for (var i = 0; i < tracks.Count; i++)
            if (IsEnglish(language(tracks[i]), title(tracks[i]))) return i;
        for (var i = 0; i < tracks.Count; i++)
            if (isDefault(tracks[i])) return i;
        return 0;
    }
}
