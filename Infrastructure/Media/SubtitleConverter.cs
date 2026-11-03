using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MovieHub.Infrastructure.Media;

/// <summary>Converts external subtitle files to WebVTT, the only text-track format browsers accept for &lt;track&gt;.</summary>
public static class SubtitleConverter
{
    private static readonly Regex SrtTime = new(@"(\d{1,2}:\d{2}:\d{2}),(\d{1,3})", RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"\{[^}]*\}", RegexOptions.Compiled);

    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes); // legacy single-byte subtitle files
        }
    }

    public static string ToVtt(string text, string format)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿');
        return format.TrimStart('.').ToLowerInvariant() switch
        {
            "vtt" => text.StartsWith("WEBVTT", StringComparison.Ordinal) ? text : "WEBVTT\n\n" + text,
            "srt" => SrtToVtt(text),
            "ass" or "ssa" => AssToVtt(text),
            _ => throw new NotSupportedException($"Subtitle format '{format}' cannot be converted.")
        };
    }

    public static string SrtToVtt(string srt)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var rawLine in srt.Split('\n'))
        {
            var line = rawLine;
            if (line.Contains("-->")) line = SrtTime.Replace(line, m => $"{m.Groups[1].Value}.{m.Groups[2].Value.PadRight(3, '0')}");
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    public static string AssToVtt(string ass)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        string[]? columns = null;
        var inEvents = false;
        foreach (var raw in ass.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inEvents) continue;

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                columns = line[7..].Split(',').Select(c => c.Trim()).ToArray();
                continue;
            }
            if (columns == null || !line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;

            var fields = line[9..].TrimStart().Split(',', columns.Length);
            if (fields.Length < columns.Length) continue;
            var start = Time(fields[Array.IndexOf(columns, "Start")]);
            var end = Time(fields[Array.IndexOf(columns, "End")]);
            var textIdx = Array.IndexOf(columns, "Text");
            if (start == null || end == null || textIdx < 0) continue;

            var cue = Tags.Replace(fields[textIdx], "").Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ").Trim();
            if (cue.Length == 0) continue;
            sb.Append(start).Append(" --> ").Append(end).Append('\n').Append(cue).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string? Time(string s)
    {
        // ASS time: H:MM:SS.cc
        var m = Regex.Match(s.Trim(), @"^(\d+):(\d{2}):(\d{2})\.(\d{2})$");
        if (!m.Success) return null;
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return $"{h:00}:{m.Groups[2].Value}:{m.Groups[3].Value}.{m.Groups[4].Value}0";
    }

    public static string? GuessLanguage(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var tokens = Regex.Split(stem, @"[\s._\-\[\]\(\)]+").Where(t => t.Length > 0);
        foreach (var t in tokens.Reverse())
        {
            var lang = FfprobeRunner.NormalizeLanguage(t.Length is 2 or 3 ? t : null);
            if (lang != null && lang != t) return lang;
            if (t.Equals("English", StringComparison.OrdinalIgnoreCase) || t.Equals("Hindi", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Bengali", StringComparison.OrdinalIgnoreCase) || t.Equals("Arabic", StringComparison.OrdinalIgnoreCase))
                return char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant();
        }
        return null;
    }
}
