using Microsoft.Extensions.Options;
using StreamApp.Configuration;

namespace StreamApp.Infrastructure.Media;

/// <summary>
/// Finds the ffmpeg executable (see <see cref="ToolLocator"/>): project tools folder first, then PATH.
/// Server-side audio switching is available exactly when this resolves.
/// </summary>
public sealed class FfmpegTool
{
    private readonly MediaOptions _opt;
    private readonly ILogger<FfmpegTool> _log;
    private readonly ToolDirectories _dirs;
    private readonly object _gate = new();
    private bool _detected;
    private ToolInfo? _info;

    public FfmpegTool(IOptions<MediaOptions> opt, ILogger<FfmpegTool> log, ToolDirectories? dirs = null)
    {
        _opt = opt.Value;
        _log = log;
        _dirs = dirs ?? ToolDirectories.Default();
    }

    /// <summary>The runnable ffmpeg command (a full path or a bare name found on PATH); null when none works.</summary>
    public string? Path { get { Detect(); return _info?.Command; } }
    public string? Version { get { Detect(); return _info?.Version; } }
    public bool IsAvailable => Path != null;

    /// <summary>Forget the cached result and look again (used after the downloader installs the tools).</summary>
    public void Reset() { lock (_gate) { _detected = false; _info = null; } Detect(); }

    public void Detect()
    {
        if (_detected) return;
        lock (_gate)
        {
            if (_detected) return;
            _info = ToolLocator.Find("ffmpeg", _opt.FfmpegPath, _dirs, new[] { _opt.FfprobePath });
            if (_info != null) _log.LogInformation("ffmpeg found ({Path}): server-side audio switching is available.", _info.Command);
            else if (!string.IsNullOrWhiteSpace(_opt.FfmpegPath)) _log.LogWarning("Media:FfmpegPath '{Path}' could not be run.", _opt.FfmpegPath);
            else _log.LogWarning("ffmpeg was not found in {Dir} or on PATH. Audio-track switching and MKV/HEVC/AC3 playback will not work. Use Admin > Dashboard > Download FFmpeg, or copy ffmpeg into that folder.", _dirs.Primary);
            _detected = true;
        }
    }
}
