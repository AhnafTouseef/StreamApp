using System.Diagnostics;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;

namespace MovieHub.Infrastructure.Media;

/// <summary>A video encoder that runs on the graphics hardware, with the ffmpeg arguments that select it.</summary>
/// <param name="Kind">nvenc, qsv, amf, vaapi or custom.</param>
/// <param name="Name">Shown to people, e.g. "Intel Quick Sync".</param>
/// <param name="DecodeArgs">Input options (placed before -i) that let the graphics hardware decode the source too.</param>
/// <param name="VideoArgs">Replaces the software encoder's arguments.</param>
public sealed record HwEncoderInfo(string Kind, string Name, string[] DecodeArgs, string[] VideoArgs);

/// <summary>
/// Finds out whether this machine can encode the picture on its graphics hardware (so a re-encode does not eat the CPU) and keeps the answer.
/// "auto" really tries each candidate encoder with a half-second test encode, because an ffmpeg build that lists an encoder says nothing about whether the machine can run it.
/// If a stream later fails on the hardware encoder, it is switched off for a while and the software encoder is used (see <see cref="ReportFailure"/>).
/// </summary>
public sealed class HardwareEncoder
{
    private static readonly TimeSpan DisableFor = TimeSpan.FromMinutes(10);
    private readonly MediaOptions _opt;
    private readonly FfmpegTool _ffmpeg;
    private readonly ILogger<HardwareEncoder> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile string? _forPath;
    private volatile HwEncoderInfo? _info;
    private long _disabledUntilTicks;

    public HardwareEncoder(IOptions<MediaOptions> opt, FfmpegTool ffmpeg, ILogger<HardwareEncoder> log) { _opt = opt.Value; _ffmpeg = ffmpeg; _log = log; }

    /// <summary>The usable hardware encoder, or null (none found, switched off, or not looked for yet). Never blocks.</summary>
    public HwEncoderInfo? Current => DateTime.UtcNow.Ticks < Interlocked.Read(ref _disabledUntilTicks) ? null : _info;

    /// <summary>Looks for a hardware encoder once per ffmpeg (again when the admin installs another ffmpeg) and returns it, or null for software encoding.</summary>
    public async Task<HwEncoderInfo?> GetAsync(CancellationToken ct = default)
    {
        var path = _ffmpeg.Path;
        if (path == null) return null;
        if (_forPath == path) return Current;
        await _lock.WaitAsync(ct);
        try
        {
            if (_forPath == path) return Current;
            _info = await DetectAsync(path, ct);
            _forPath = path;
            Interlocked.Exchange(ref _disabledUntilTicks, 0);
        }
        finally { _lock.Release(); }
        return Current;
    }

    /// <summary>A stream failed on the hardware encoder but worked with the software one: stop using the hardware encoder for a while.</summary>
    public void ReportFailure(HwEncoderInfo info, string why)
    {
        Interlocked.Exchange(ref _disabledUntilTicks, DateTime.UtcNow.Add(DisableFor).Ticks);
        _log.LogWarning("Hardware encoder {Name} failed ({Why}); using the software encoder for the next {Minutes} minutes.", info.Name, why, (int)DisableFor.TotalMinutes);
    }

    // ------------------------------------------------------------------ the encoders

    /// <summary>Candidates for "auto", best first for this operating system.</summary>
    internal static string[] Candidates(bool windows) => windows ? new[] { "nvenc", "qsv", "amf" } : new[] { "nvenc", "qsv", "vaapi" };

    internal static string DisplayName(string kind) => kind switch
    {
        "nvenc" => "NVIDIA NVENC", "qsv" => "Intel Quick Sync", "amf" => "AMD AMF", "vaapi" => "VAAPI", _ => "custom hardware encoder"
    };

    /// <summary>The ffmpeg arguments that replace "-c:v libx264 …". Quality is aimed at roughly the software setting (CRF 23); a short GOP keeps fragments small so playback starts quickly.</summary>
    internal static string[] VideoArgs(string kind, string? custom = null) => kind switch
    {
        "nvenc" => new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "24", "-b:v", "0", "-g", "72", "-pix_fmt", "yuv420p" },
        "qsv" => new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "24", "-g", "72", "-pix_fmt", "nv12" },
        "amf" => new[] { "-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", "24", "-qp_p", "26", "-qp_b", "28", "-g", "72", "-pix_fmt", "nv12" },
        "vaapi" => new[] { "-vaapi_device", "/dev/dri/renderD128", "-vf", "format=nv12,hwupload", "-c:v", "h264_vaapi", "-qp", "24", "-g", "72" },
        "custom" => (custom ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => Array.Empty<string>()
    };

    private HwEncoderInfo Make(string kind) =>
        new(kind, DisplayName(kind), _opt.HardwareDecode ? new[] { "-hwaccel", "auto" } : Array.Empty<string>(), VideoArgs(kind, _opt.HardwareEncoderArgs));

    private async Task<HwEncoderInfo?> DetectAsync(string ffmpegPath, CancellationToken ct)
    {
        var setting = (_opt.HardwareEncoder ?? "auto").Trim().ToLowerInvariant();
        if (setting is "none" or "off" or "software" or "")
        {
            _log.LogInformation("Hardware video encoding is switched off (Media:HardwareEncoder); re-encoding uses the CPU.");
            return null;
        }
        if (setting == "custom")
        {
            var info = Make("custom");
            if (info.VideoArgs.Length == 0) { _log.LogWarning("Media:HardwareEncoder is custom but Media:HardwareEncoderArgs is empty; using the software encoder."); return null; }
            _log.LogInformation("Hardware video encoder: custom ({Args}). Not tested at startup; a stream that fails on it falls back to the software encoder.", string.Join(' ', info.VideoArgs));
            return info;
        }

        var kinds = setting == "auto" ? Candidates(OperatingSystem.IsWindows()) : new[] { setting };
        foreach (var kind in kinds)
        {
            var info = Make(kind);
            if (info.VideoArgs.Length == 0) { _log.LogWarning("Media:HardwareEncoder \"{Setting}\" is not a known encoder (use auto, none, nvenc, qsv, amf, vaapi or custom).", setting); return null; }
            var why = await TestEncodeAsync(ffmpegPath, info, ct);
            if (why == null)
            {
                _log.LogInformation("Hardware video encoder: {Name}. Re-encoding runs on the graphics hardware instead of the CPU.", info.Name);
                return info;
            }
            _log.LogInformation("Hardware video encoder {Name} is not usable here: {Why}", info.Name, why);
        }
        _log.LogInformation("No hardware video encoder found; re-encoding uses the CPU (libx264).");
        return null;
    }

    /// <summary>Encodes half a second of generated video with the candidate. Null when it worked, otherwise the reason.</summary>
    internal static async Task<string?> TestEncodeAsync(string ffmpegPath, HwEncoderInfo info, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(new[] { "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=24", "-t", "0.5" });
        args.AddRange(info.VideoArgs);
        args.AddRange(new[] { "-f", "null", "-" });
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
            _ = proc.StandardOutput.ReadToEndAsync();
            var err = proc.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch (InvalidOperationException) { } if (ct.IsCancellationRequested) throw; return "the test encode did not finish in time"; }
            if (proc.ExitCode == 0) return null;
            var text = (await err).Trim().Split('\n').LastOrDefault()?.Trim();
            return string.IsNullOrEmpty(text) ? $"ffmpeg exited with code {proc.ExitCode}" : text;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return ex.Message; }
    }
}

/// <summary>Looks for the hardware encoder in the background once the app is up, so the first re-encode does not wait for the test.</summary>
public sealed class HardwareEncoderWarmup : BackgroundService
{
    private readonly HardwareEncoder _hw;
    private readonly ILogger<HardwareEncoderWarmup> _log;
    public HardwareEncoderWarmup(HardwareEncoder hw, ILogger<HardwareEncoderWarmup> log) { _hw = hw; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Yield(); await _hw.GetAsync(stoppingToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Could not look for a hardware video encoder; re-encoding uses the CPU."); }
    }
}
