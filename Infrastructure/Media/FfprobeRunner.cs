using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;

namespace MovieHub.Infrastructure.Media;

public sealed record ProbeStream(
    int Index, string Type, string? Codec, string? Language, string? Title, bool IsDefault,
    int? Channels, int? Width, int? Height);

public sealed record ProbeResult(
    bool Success, string? Error, double? DurationSeconds, long? SizeBytes,
    int? Width, int? Height, IReadOnlyList<ProbeStream> Streams)
{
    public static ProbeResult Fail(string error) => new(false, error, null, null, null, null, Array.Empty<ProbeStream>());
}

/// <summary>Runs ffprobe against a (remote) URL. It reads only container headers/indexes over HTTP range requests — never the whole file.</summary>
public sealed class FfprobeRunner
{
    private readonly MediaOptions _opt;
    private readonly ILogger<FfprobeRunner> _log;

    public FfprobeRunner(IOptions<MediaOptions> opt, ILogger<FfprobeRunner> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_opt.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[]
                 {
                     "-v", "error", "-print_format", "json", "-show_format", "-show_streams",
                     "-protocol_whitelist", "http,https,tcp,tls,crypto", "-rw_timeout", "30000000", "-user_agent", "MovieHub/1.0", url
                 })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ProbeResult.Fail($"ffprobe could not be started from '{_opt.FfprobePath}': {ex.Message}");
        }

        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _opt.ProbeTimeoutSeconds)));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            if (ct.IsCancellationRequested) throw;
            return ProbeResult.Fail($"ffprobe timed out after {_opt.ProbeTimeoutSeconds}s.");
        }

        var json = await stdout;
        var err = (await stderr).Trim();
        if (proc.ExitCode != 0)
            return ProbeResult.Fail(string.IsNullOrEmpty(err) ? $"ffprobe exited with code {proc.ExitCode}." : Truncate(err, 400));

        try
        {
            return Parse(json);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "ffprobe produced unreadable output for {Url}", url);
            return ProbeResult.Fail("ffprobe output could not be parsed.");
        }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { /* already gone */ }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double? duration = null;
        long? size = null;
        if (root.TryGetProperty("format", out var format))
        {
            duration = ReadDouble(format, "duration");
            if (ReadDouble(format, "size") is { } sz) size = (long)sz;
        }

        var streams = new List<ProbeStream>();
        int? width = null, height = null;
        if (root.TryGetProperty("streams", out var arr))
        {
            foreach (var s in arr.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                var index = s.TryGetProperty("index", out var i) ? i.GetInt32() : streams.Count;

                string? lang = null, title = null;
                if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in tags.EnumerateObject())
                    {
                        if (p.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) lang = p.Value.GetString();
                        else if (p.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) title = p.Value.GetString();
                    }
                }

                var isDefault = false;
                var attachedPic = false;
                if (s.TryGetProperty("disposition", out var disp) && disp.ValueKind == JsonValueKind.Object)
                {
                    isDefault = disp.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt32() == 1;
                    attachedPic = disp.TryGetProperty("attached_pic", out var ap) && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1;
                }
                if (attachedPic) continue; // embedded cover art is not a video track

                int? w = s.TryGetProperty("width", out var wv) && wv.ValueKind == JsonValueKind.Number ? wv.GetInt32() : null;
                int? h = s.TryGetProperty("height", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : null;
                int? ch = s.TryGetProperty("channels", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetInt32() : null;
                var codec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;

                if (type == "video" && width == null) { width = w; height = h; }
                streams.Add(new ProbeStream(index, type, codec, NormalizeLanguage(lang), title, isDefault, ch, w, h));
            }
        }
        return new ProbeResult(true, null, duration, size, width, height, streams);
    }

    private static double? ReadDouble(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String &&
               double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "English", ["en"] = "English", ["hin"] = "Hindi", ["hi"] = "Hindi", ["tam"] = "Tamil", ["tel"] = "Telugu",
        ["mal"] = "Malayalam", ["kan"] = "Kannada", ["ben"] = "Bengali", ["bn"] = "Bengali", ["kor"] = "Korean", ["jpn"] = "Japanese",
        ["fra"] = "French", ["fre"] = "French", ["spa"] = "Spanish", ["deu"] = "German", ["ger"] = "German", ["ita"] = "Italian",
        ["rus"] = "Russian", ["zho"] = "Chinese", ["chi"] = "Chinese", ["ara"] = "Arabic", ["por"] = "Portuguese", ["tur"] = "Turkish",
        ["tha"] = "Thai", ["urd"] = "Urdu", ["pan"] = "Punjabi", ["mar"] = "Marathi", ["ind"] = "Indonesian", ["nld"] = "Dutch",
        ["dut"] = "Dutch", ["swe"] = "Swedish", ["pol"] = "Polish", ["heb"] = "Hebrew", ["vie"] = "Vietnamese", ["fas"] = "Persian",
        ["per"] = "Persian", ["ell"] = "Greek", ["gre"] = "Greek"
    };

    /// <summary>Maps ISO-639 codes to readable names; "und" (undetermined) becomes null.</summary>
    public static string? NormalizeLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim();
        if (code.Equals("und", StringComparison.OrdinalIgnoreCase) || code.Equals("unk", StringComparison.OrdinalIgnoreCase)) return null;
        return Languages.TryGetValue(code, out var name) ? name : code;
    }
}
