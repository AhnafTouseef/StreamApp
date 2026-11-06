using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;
using MovieHub.Infrastructure.Crawler;
using MovieHub.Infrastructure.Media;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Endpoints;

/// <summary>
/// Playback helpers. The player first tries the ISP URL directly. These endpoints are the fallbacks:
///   /media/stream/{id}    range-aware pass-through proxy (CORS / mixed-content / blocked direct access); never buffers the file
///   /media/remux/{id}     optional ffmpeg pipe for audio-track switching and unsupported containers/codecs
///   /media/subtitle/{id}  external subtitle converted to WebVTT and served same-origin (a cross-origin track would be blocked)
///   /media/embsub/{id}    subtitle tracks stored inside the video: status (starts the one-time extraction) and the cached WebVTT per track
///   /media/probe/{id}     admin only: shows what the ISP server actually answers (range support, CORS headers)
/// </summary>
public static class MediaEndpoints
{
    public const string StreamClientName = "stream";
    private static readonly SemaphoreSlim RemuxSlots = new(1, 1); // replaced in Map with the configured size
    private static SemaphoreSlim _remux = RemuxSlots;

    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var opt = app.ServiceProvider.GetRequiredService<IOptions<MediaOptions>>().Value;
        _remux = new SemaphoreSlim(Math.Max(1, opt.MaxRemuxSessions));

        var g = app.MapGroup("/media");
        g.MapMethods("/stream/{fileId:int}", new[] { "GET", "HEAD" }, StreamAsync);
        g.MapGet("/remux/{fileId:int}", RemuxAsync);
        g.MapGet("/subtitle/{id:int}", SubtitleAsync);
        g.MapGet("/embsub/{fileId:int}", EmbeddedStatusAsync);
        g.MapGet("/embsub/{fileId:int}/window", EmbeddedWindowAsync);
        g.MapGet("/embsub/{fileId:int}/{index:int}", EmbeddedTrackAsync);
        g.MapGet("/probe/{fileId:int}", ProbeAsync).RequireAuthorization();
        g.MapPost("/inspect/{fileId:int}", InspectAsync);
    }

    // ------------------------------------------------------------------ pass-through proxy with ranges

    private static readonly string[] ForwardResponseHeaders =
        { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Last-Modified", "ETag" };

    private static async Task StreamAsync(
        int fileId, HttpContext ctx, IMediaLookup lookup, IHttpClientFactory http,
        SourcePolicy policy, ILogger<MediaLog> log)
    {
        var url = (await lookup.GetFileAsync(fileId, ctx.RequestAborted))?.Url;
        if (url == null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (!policy.IsAllowed(url)) { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return; }

        var client = http.CreateClient(StreamClientName);
        var current = new Uri(url);
        HttpResponseMessage? resp = null;
        try
        {
            for (var hop = 0; hop <= 3; hop++)
            {
                if (!policy.IsAllowed(current)) { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return; }

                using var req = new HttpRequestMessage(HttpMethods.IsHead(ctx.Request.Method) ? HttpMethod.Head : HttpMethod.Get, current);
                foreach (var h in new[] { "Range", "If-Range", "If-Modified-Since", "If-None-Match" })
                    if (ctx.Request.Headers.TryGetValue(h, out var v)) req.Headers.TryAddWithoutValidation(h, v.ToArray());

                resp?.Dispose();
                resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
                {
                    current = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                    continue;
                }
                break;
            }
            if (resp == null) { ctx.Response.StatusCode = StatusCodes.Status502BadGateway; return; }

            ctx.Response.StatusCode = (int)resp.StatusCode;
            var ok = resp.IsSuccessStatusCode || (int)resp.StatusCode == 304;
            foreach (var name in ForwardResponseHeaders)
            {
                // Error responses are relayed as status only: their (HTML) body is not forwarded, so their Content-Length must not be either.
                if (!ok && name is not "Content-Range") continue;
                if (resp.Headers.TryGetValues(name, out var vals) || resp.Content.Headers.TryGetValues(name, out vals))
                    ctx.Response.Headers[name] = vals.ToArray();
            }
            if (ok && !ctx.Response.Headers.ContainsKey("Accept-Ranges")) ctx.Response.Headers["Accept-Ranges"] = "bytes";
            if (HttpMethods.IsHead(ctx.Request.Method) || !ok || (int)resp.StatusCode == 304) return;

            await using var upstream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
            await upstream.CopyToAsync(ctx.Response.Body, 81920, ctx.RequestAborted); // streamed in 80 KB chunks, never held in memory
        }
        catch (OperationCanceledException) { /* viewer closed the tab or seeked */ }
        catch (HttpRequestException ex)
        {
            log.LogWarning("Stream proxy could not reach {Url}: {Message}", url, ex.Message);
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
        finally
        {
            resp?.Dispose();
        }
    }

    // ------------------------------------------------------------------ ffmpeg remux (optional)

    private static async Task<IResult> RemuxAsync(
        int fileId, HttpContext ctx, IMediaLookup lookup, FfmpegTool ffmpeg,
        SourcePolicy policy, ILogger<MediaLog> log, int? audio = null, double t = 0, string? video = null)
    {
        if (!ffmpeg.IsAvailable)
            return Results.Problem("Server-side audio switching is unavailable: ffmpeg was not found. Install FFmpeg or set Media:FfmpegPath.", statusCode: 501);

        var file = await lookup.GetFileAsync(fileId, ctx.RequestAborted);
        if (file == null) return Results.NotFound();
        if (!policy.IsAllowed(file.Url)) return Results.StatusCode(403);
        if (t < 0 || double.IsNaN(t) || double.IsInfinity(t)) return Results.BadRequest("Invalid start time.");

        var audioTracks = file.Streams.Where(s => s.Kind == StreamKind.Audio).OrderBy(s => s.Index).ToList();
        StreamRow? audioStream;
        if (audio.HasValue) audioStream = audioTracks.FirstOrDefault(s => s.Index == audio.Value);
        else
        {
            // No explicit choice: start with English when the file has an English track, otherwise the file's default.
            var pick = AudioPreference.Choose(audioTracks, s => s.Language, s => s.Title, s => s.IsDefault);
            audioStream = pick >= 0 ? audioTracks[pick] : null;
        }
        var videoStream = file.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        if (audio.HasValue && audioStream == null) return Results.BadRequest("Unknown audio stream.");

        if (!await _remux.WaitAsync(0, ctx.RequestAborted))
            return Results.Problem("Too many compatibility streams are running. Try again shortly.", statusCode: 503);

        Process? proc = null;
        try
        {
            // video=encode: the browser could not show the copied picture (10-bit H.264, odd profiles), so the picture is re-encoded as plain 8-bit H.264.
            var copyVideo = videoStream?.Codec is "h264" or "avc1" && !string.Equals(video, "encode", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(ffmpeg.Path!)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            // The protocol whitelist stops a redirect from making ffmpeg read local files or other protocols.
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "http,https,tcp,tls,crypto" };
            if (t > 0) { args.Add("-ss"); args.Add(t.ToString("0.###", CultureInfo.InvariantCulture)); }
            args.AddRange(new[] { "-i", file.Url, "-map", "0:v:0?" });
            args.AddRange(new[] { "-map", audioStream != null ? $"0:{audioStream.Index}" : "0:a:0?" });
            args.AddRange(copyVideo
                ? new[] { "-c:v", "copy" }
                : new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p" });
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-sn", "-dn",
                "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "pipe:1" });
            foreach (var a in args) psi.ArgumentList.Add(a);

            proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
            _ = proc.StandardError.ReadToEndAsync().ContinueWith(
                r => { if (r.IsCompletedSuccessfully && r.Result.Length > 0) log.LogWarning("ffmpeg: {Error}", r.Result.Trim()); });

            ctx.Response.ContentType = "video/mp4";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            await proc.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, 81920, ctx.RequestAborted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.LogWarning("Remux failed for file {FileId}: {Message}", fileId, ex.Message);
            if (!ctx.Response.HasStarted) return Results.Problem("Could not start ffmpeg: " + ex.Message, statusCode: 500);
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(true); } catch { /* already finished */ }
            proc?.Dispose();
            _remux.Release();
        }
        return Results.Empty;
    }

    // ------------------------------------------------------------------ subtitles

    private static async Task<IResult> SubtitleAsync(
        int id, HttpContext ctx, IMediaLookup lookup, IHttpClientFactory http,
        SourcePolicy policy, ILogger<MediaLog> log)
    {
        var sub = await lookup.GetSubtitleAsync(id, ctx.RequestAborted);
        if (sub == null) return Results.NotFound();
        if (!policy.IsAllowed(sub.Url)) return Results.StatusCode(403);

        try
        {
            using var resp = await http.CreateClient(StreamClientName).GetAsync(sub.Url, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            if (!resp.IsSuccessStatusCode) return Results.StatusCode(502);
            if (resp.Content.Headers.ContentLength > 3_000_000) return Results.StatusCode(413);

            var bytes = await resp.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            var vtt = SubtitleConverter.ToVtt(SubtitleConverter.DecodeText(bytes), sub.Format);
            ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
            return Results.Text(vtt, "text/vtt; charset=utf-8");
        }
        catch (NotSupportedException) { return Results.StatusCode(415); }
        catch (HttpRequestException ex)
        {
            log.LogWarning("Subtitle {Url} unavailable: {Message}", sub.Url, ex.Message);
            return Results.StatusCode(502);
        }
    }

    // ------------------------------------------------------------------ subtitles stored inside the video file

    private static async Task<IResult> EmbeddedStatusAsync(int fileId, HttpContext ctx, IMediaLookup lookup, EmbeddedSubtitleService embedded, SourcePolicy policy)
    {
        if (!ctx.Request.Headers.ContainsKey("X-Requested-With")) return Results.BadRequest("Missing header.");   // starts a background job: not for foreign pages
        var file = await lookup.GetFileAsync(fileId, ctx.RequestAborted);
        if (file == null) return Results.NotFound();
        if (!policy.IsAllowed(file.Url)) return Results.StatusCode(403);
        ctx.Response.Headers["Cache-Control"] = "no-store";
        return Results.Json(new
        {
            tracks = embedded.Describe(file).Select(t => new { index = t.Index, lang = t.Language, title = t.Title, codec = t.Codec, isDefault = t.IsDefault, supported = t.Supported, state = t.State })
        });
    }

    private static async Task<IResult> EmbeddedWindowAsync(int fileId, double? from, double? len, HttpContext ctx, IMediaLookup lookup, EmbeddedSubtitleService embedded, SourcePolicy policy)
    {
        if (!ctx.Request.Headers.ContainsKey("X-Requested-With")) return Results.BadRequest("Missing header.");
        var file = await lookup.GetFileAsync(fileId, ctx.RequestAborted);
        if (file == null) return Results.NotFound();
        if (!policy.IsAllowed(file.Url)) return Results.StatusCode(403);
        try
        {
            var w = await embedded.ReadWindowAsync(file, from ?? 0, len is > 0 ? len.Value : 600, ctx.RequestAborted);
            ctx.Response.Headers["Cache-Control"] = "no-store";
            return Results.Json(new
            {
                from = w.From, length = w.Length,
                tracks = w.Tracks.Select(t => new { index = t.Index, lang = t.Language, title = t.Title, codec = t.Codec, isDefault = t.IsDefault, supported = t.Supported, vtt = t.Vtt })
            });
        }
        catch (OperationCanceledException) { return Results.StatusCode(499); }
        catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 502); }
    }

    private static async Task<IResult> EmbeddedTrackAsync(int fileId, int index, HttpContext ctx, IMediaLookup lookup, EmbeddedSubtitleService embedded)
    {
        var file = await lookup.GetFileAsync(fileId, ctx.RequestAborted);
        var stream = file?.Streams.FirstOrDefault(s => s.Kind == StreamKind.Subtitle && s.Index == index);
        if (file == null || stream == null || !EmbeddedSubtitleService.IsText(stream.Codec)) return Results.NotFound();   // only tracks the database knows about: the index never becomes part of an arbitrary path
        var path = embedded.PathFor(file, index);
        if (!File.Exists(path)) return Results.NotFound();
        ctx.Response.Headers["Cache-Control"] = "private, max-age=600";   // the player keeps its own copy in the browser; the staged file disappears from the server soon
        return Results.File(path, "text/vtt; charset=utf-8");
    }

    // ------------------------------------------------------------------ on-demand inspection

    /// <summary>
    /// The player calls this when a file has no usable stream information (ffprobe failed or never ran at scan time), so audio tracks appear
    /// without an admin having to re-scan. Idempotent and throttled per file; the custom header keeps it from being triggered by a foreign page.
    /// </summary>
    private static async Task<IResult> InspectAsync(int fileId, HttpContext ctx, InspectionBackfill backfill)
    {
        if (!ctx.Request.Headers.ContainsKey("X-Requested-With")) return Results.BadRequest("Missing header.");
        var snap = await backfill.InspectOneAsync(fileId, ctx.RequestAborted);
        if (snap == null) return Results.NotFound();
        return Results.Json(new
        {
            inspected = snap.Inspected, inspectionError = snap.Error, duration = snap.Duration, videoCodec = snap.VideoCodec, audioCodec = snap.AudioCodec,
            audio = snap.Audio.Select(a => new { index = a.Index, lang = a.Lang, title = a.Title, codec = a.Codec, channels = a.Channels, isDefault = a.IsDefault }),
            embeddedSubtitles = snap.EmbeddedSubtitles, preferredAudio = snap.PreferredAudio, defaultAudio = snap.DefaultAudio
        });
    }

    // ------------------------------------------------------------------ admin probe

    private static async Task<IResult> ProbeAsync(int fileId, HttpContext ctx, IMediaLookup lookup, DirectoryListingClient listing)
    {
        var file = await lookup.GetFileAsync(fileId, ctx.RequestAborted);
        if (file == null) return Results.NotFound();
        return Results.Json(await listing.ProbeAsync(file.Url, ctx.RequestAborted));
    }

    /// <summary>Category type for the logger of the static endpoint class.</summary>
    public sealed class MediaLog { }
}
