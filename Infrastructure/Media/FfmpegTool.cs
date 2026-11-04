using System.Diagnostics;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;

namespace MovieHub.Infrastructure.Media;

/// <summary>
/// Finds the ffmpeg executable once at startup. Order: Media:FfmpegPath, then "ffmpeg" next to the configured ffprobe,
/// then "ffmpeg" on PATH. FFprobe is already required, and ffmpeg ships in the same package, so audio switching works
/// out of the box instead of needing a second setting. Server-side audio switching is available exactly when this resolves.
/// </summary>
public sealed class FfmpegTool
{
    private readonly MediaOptions _opt;
    private readonly ILogger<FfmpegTool> _log;
    private readonly object _gate = new();
    private bool _detected;
    private string? _path;

    public FfmpegTool(IOptions<MediaOptions> opt, ILogger<FfmpegTool> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    /// <summary>The runnable ffmpeg command (a full path or a bare name found on PATH); null when none works.</summary>
    public string? Path { get { Detect(); return _path; } }

    public bool IsAvailable => Path != null;

    public void Detect()
    {
        if (_detected) return;
        lock (_gate)
        {
            if (_detected) return;
            foreach (var candidate in Candidates())
            {
                if (!Works(candidate)) continue;
                _path = candidate;
                _log.LogInformation("ffmpeg found ({Path}): server-side audio switching is available.", candidate);
                break;
            }
            if (_path == null)
                _log.LogWarning("ffmpeg was not found (tried Media:FfmpegPath, next to ffprobe, and PATH). Audio-track switching in Chrome/Edge/Firefox and playback of MKV/HEVC/AC3 files will not work. Install FFmpeg or set Media:FfmpegPath.");
            _detected = true;
        }
    }

    private IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrWhiteSpace(_opt.FfmpegPath)) yield return _opt.FfmpegPath.Trim();

        var probe = _opt.FfprobePath?.Trim();
        if (!string.IsNullOrEmpty(probe))
        {
            var dir = System.IO.Path.GetDirectoryName(probe);
            if (!string.IsNullOrEmpty(dir))
            {
                var ext = System.IO.Path.GetExtension(probe);
                yield return System.IO.Path.Combine(dir, "ffmpeg" + ext);
            }
        }
        yield return "ffmpeg";
    }

    private static bool Works(string command)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            if (p == null) return false;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { /* gone */ } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false; // not installed / not executable
        }
    }
}
