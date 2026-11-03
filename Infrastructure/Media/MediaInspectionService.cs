using Microsoft.Extensions.Options;
using MovieHub.Configuration;
using MovieHub.Models;

namespace MovieHub.Infrastructure.Media;

/// <summary>Runs ffprobe for media files and stores the result (duration, codecs, resolution, streams). Failures are recorded, never thrown.</summary>
public sealed class MediaInspectionService
{
    private readonly FfprobeRunner _probe;
    private readonly SemaphoreSlim _gate;
    private readonly ILogger<MediaInspectionService> _log;

    public MediaInspectionService(FfprobeRunner probe, IOptions<MediaOptions> opt, ILogger<MediaInspectionService> log)
    {
        _probe = probe;
        _log = log;
        _gate = new SemaphoreSlim(Math.Max(1, opt.Value.MaxConcurrentProbes));
    }

    public static string Fingerprint(long? size, DateTime? modified) => $"{size?.ToString() ?? "?"}-{modified?.Ticks.ToString() ?? "?"}";

    /// <summary>Inspects <paramref name="file"/> in place. The caller saves the context. Existing streams must be loaded when the file is already tracked.</summary>
    public async Task InspectAsync(MediaFile file, CancellationToken ct)
    {
        ProbeResult result;
        await _gate.WaitAsync(ct);
        try
        {
            result = await _probe.ProbeAsync(file.FileUrl, ct);
        }
        finally
        {
            _gate.Release();
        }

        file.InspectedAt = DateTime.UtcNow;
        if (!result.Success)
        {
            file.InspectionError = result.Error;
            _log.LogWarning("ffprobe failed for {File}: {Error}", file.FileName, result.Error);
            return;
        }

        file.InspectionError = null;
        file.Duration = result.DurationSeconds;
        file.FileSize ??= result.SizeBytes;
        file.Resolution = result.Width is { } w && result.Height is { } h ? $"{w}x{h}" : null;
        file.VideoCodec = result.Streams.FirstOrDefault(s => s.Type == "video")?.Codec;
        file.AudioCodec = result.Streams.FirstOrDefault(s => s.Type == "audio")?.Codec;

        file.Streams.Clear();
        foreach (var s in result.Streams)
        {
            file.Streams.Add(new MediaStream
            {
                StreamIndex = s.Index,
                StreamType = s.Type switch { "video" => StreamKind.Video, "audio" => StreamKind.Audio, "subtitle" => StreamKind.Subtitle, _ => StreamKind.Other },
                Codec = s.Codec,
                Language = s.Language,
                Title = s.Title,
                IsDefault = s.IsDefault,
                Channels = s.Channels
            });
        }
    }
}
