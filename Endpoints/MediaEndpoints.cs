using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using StreamApp.Configuration;
using StreamApp.Infrastructure.Crawler;
using StreamApp.Infrastructure.Media;
using StreamApp.Models;
using StreamApp.Services;

namespace StreamApp.Endpoints;

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
    // Streams whose picture is only copied cost a few percent of a core each: no limit unless Media:MaxRemuxSessions asks for one (null = unlimited).
    // Streams that re-encode the picture really use the CPU, so they share a budget sized from the CPU count (Media:MaxEncodeSessions); they wait briefly for a slot.
    private static SemaphoreSlim? _copySlots;
    private static SemaphoreSlim? _encodeSlots;
    private static SemaphoreSlim? _hwEncodeSlots;   // re-encodes done by the graphics hardware: the CPU is mostly free, so the budget is larger
    private static readonly TimeSpan EncodeSlotWait = TimeSpan.FromSeconds(8);
    private static bool _alwaysEncode;
    private static bool _copyAudio = true;

    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var opt = app.ServiceProvider.GetRequiredService<IOptions<MediaOptions>>().Value;
        _copySlots = opt.MaxRemuxSessions > 0 ? new SemaphoreSlim(opt.MaxRemuxSessions) : null;
        var encodeLimit = opt.MaxEncodeSessions > 0 ? opt.MaxEncodeSessions : opt.MaxEncodeSessions < 0 ? 0 : Math.Max(1, Environment.ProcessorCount / 2);
        _encodeSlots = encodeLimit > 0 ? new SemaphoreSlim(encodeLimit) : null;
        var hwLimit = opt.MaxEncodeSessions > 0 ? opt.MaxEncodeSessions : opt.MaxEncodeSessions < 0 ? 0 : Math.Max(2, Environment.ProcessorCount);
        _hwEncodeSlots = hwLimit > 0 ? new SemaphoreSlim(hwLimit) : null;
        _alwaysEncode = opt.AlwaysEncodeVideo;
        _copyAudio = opt.CopyAudio;

        var g = app.MapGroup("/media");
        g.MapMethods("/stream/{fileId:int}", new[] { "GET", "HEAD" }, StreamAsync);
        g.MapGet("/remux/{fileId:int}", RemuxAsync);
        g.MapGet("/remux/{fileId:int}/start", RemuxStartAsync);
        g.MapGet("/remux/{fileId:int}/report", RemuxReport);
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
        int fileId, HttpContext ctx, IMediaLookup lookup, FfmpegTool ffmpeg, HardwareEncoder hardware,
        SourcePolicy policy, ILogger<MediaLog> log, int? audio = null, double t = 0, string? video = null, double? k = null, string? hevc = null, string? acopy = null, string? s = null)
    {
        if (!ffmpeg.IsAvailable)
            return Results.Problem("Server-side audio switching is unavailable: ffmpeg was not found. Install FFmpeg or set Media:FfmpegPath.", statusCode: 501);

        var file = await lookup.GetFileAsync(fileId, ctx.RequestAborted);
        if (file == null) return Results.NotFound();
        if (!policy.IsAllowed(file.Url)) return Results.StatusCode(403);
        if (t < 0 || double.IsNaN(t) || double.IsInfinity(t)) return Results.BadRequest("Invalid start time.");

        // One compatibility stream per viewer and movie: a reconnect, seek or method change replaces the viewer's previous stream at once, even when that one is
        // a half-dead connection the server has not noticed yet (a phone that lost signal, a tunnel that dropped the link). Without this, such a stream
        // would keep its ffmpeg process and its slot until the operating system gives up on the socket, and the viewer's own retry could be refused as "busy".
        using var lease = await ViewerStreams.StartAsync(s, fileId, ctx.RequestAborted);
        var ct = lease?.Token ?? ctx.RequestAborted;

        var audioStream = PickAudio(file.Streams, audio);
        var videoStream = file.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        if (audio.HasValue && audioStream == null) return Results.BadRequest("Unknown audio stream.");

        // video=encode: the browser could not show the copied picture (10-bit H.264, odd profiles), so the picture is re-encoded as plain 8-bit H.264.
        // hevc=1: the browser said it can show HEVC, so an HEVC picture is copied too (otherwise only H.264 is).
        var copyVideo = CanCopyVideo(videoStream?.Codec, hevc == "1") && !_alwaysEncode && !string.Equals(video, "encode", StringComparison.OrdinalIgnoreCase);
        // A copied picture that starts after a seek needs to know where it really starts (k, from /start) so the audio can be lined up with it; without it, re-encode (exact).
        if (copyVideo && t > 0 && (k is null || k < 0 || k > t)) copyVideo = false;
        var seekCopy = copyVideo && t > 0;

        // Sound: copied as it is when the viewer asked for that (acopy=1, the "Fastest" / "Slow" speeds), the track is one a browser can play inside an MP4,
        // and its start lines up with the picture (a browser ignores a late start of one track in this kind of MP4, so the sound would play too early).
        // Anything else keeps the converted AAC, whose timing is fixed by the filter below.
        var copyAudio = false;
        string audioWhy = "not asked for";
        if (acopy == "1")
        {
            if (!_copyAudio) audioWhy = "switched off (Media:CopyAudio)";
            else if (audioStream == null) audioWhy = "no audio track";
            else if (!CanCopyAudio(audioStream.Codec, audioStream.Channels)) audioWhy = $"{audioStream.Codec ?? "unknown"} sound ({audioStream.Channels?.ToString() ?? "?"} channels) cannot be copied into an MP4 stream";
            else if (t > 0 && !copyVideo) audioWhy = "after a jump the re-encoded picture starts exactly at the jump point, but copied sound would start earlier";
            else
            {
                try
                {
                    // The seek lookup (/start) usually measured this already while it was finding the keyframe, so the stream does not have to wait for a second look.
                    if (!SyncTimes.TryGet(fileId, t, audioStream.Index, out var times))
                        times = await FirstPacketTimesAsync(ffmpeg.Path!, file.Url, t, audioStream.Index, ct);
                    var (v0, a0) = times;
                    if (v0 is { } vs && a0 is { } au && Math.Abs(au - vs) <= AudioSyncToleranceSeconds) { copyAudio = true; audioWhy = "ok"; }
                    else audioWhy = v0 is { } v1 && a0 is { } a1 ? $"the sound starts {a1 - v1:+0.00;-0.00} s from the picture" : "the start of the sound could not be measured";
                }
                catch (OperationCanceledException) { return Results.StatusCode(499); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { audioWhy = "the start of the sound could not be measured"; }
            }
        }
        log.LogInformation("Compatibility stream for file {FileId}: picture {Picture}, sound {Sound} ({Why}).", fileId, copyVideo ? "copied" : "re-encoded", copyAudio ? "copied" : "converted", audioWhy);
        var pictureWhy = copyVideo ? "ok"
            : _alwaysEncode ? "the server is set to always re-encode the picture"
            : string.Equals(video, "encode", StringComparison.OrdinalIgnoreCase) ? "the stream method asks for it"
            : !CanCopyVideo(videoStream?.Codec, hevc == "1") ? $"{(string.IsNullOrWhiteSpace(videoStream?.Codec) ? "this" : videoStream!.Codec!.ToUpperInvariant())} picture cannot be copied"
            : "a copied picture cannot start exactly where you jumped";
        var soundWhy = audioWhy switch { "not asked for" => "the stream method asks for it", "ok" => "ok", _ => audioWhy };

        // A full re-encode goes to the graphics hardware when this machine has a usable encoder (HardwareEncoder); a copied picture never needs one.
        HwEncoderInfo? hw = null;
        if (!copyVideo)
        {
            try { hw = await hardware.GetAsync(ct); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
        }

        StreamReports.Put(s, fileId, copyVideo, pictureWhy, copyAudio, soundWhy, copyVideo ? null : hw?.Name ?? "CPU");
        var slot = copyVideo ? _copySlots : hw != null ? _hwEncodeSlots : _encodeSlots;   // null: no limit
        var holdsSlot = false;
        async Task<bool> AcquireAsync(SemaphoreSlim? sem, TimeSpan wait)
        {
            if (sem == null) return true;
            holdsSlot = await sem.WaitAsync(wait, ct);
            return holdsSlot;
        }
        if (slot != null)
        {
            try { await AcquireAsync(slot, copyVideo ? TimeSpan.Zero : EncodeSlotWait); }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            if (!holdsSlot)
            {
                log.LogWarning("Compatibility stream for file {FileId} refused: all {Kind} slots are busy.", fileId, copyVideo ? "copy" : "re-encode");
                ctx.Response.Headers["Retry-After"] = "5";
                return Results.Problem(copyVideo ? "The server is at its limit of compatibility streams (Media:MaxRemuxSessions)."
                                                 : "The server is already re-encoding as many streams as it can. Try again shortly.", statusCode: 503);
            }
        }

        Process? proc = null;
        var started = Stopwatch.StartNew();
        try
        {
            // Builds the ffmpeg command. h = a hardware encoder for the re-encoded picture, or null for the software encoder (or a copied picture).
            List<string> BuildArgs(HwEncoderInfo? h)
            {
                // The protocol whitelist stops a redirect from making ffmpeg read local files or other protocols.
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "http,https,tcp,tls,crypto" };
                if (h != null) args.AddRange(h.DecodeArgs);   // let the graphics hardware decode the source as well
                if (t > 0) { args.Add("-ss"); args.Add(t.ToString("0.###", CultureInfo.InvariantCulture)); }
                if (seekCopy) args.Add("-copyts");     // keep the file's own timestamps: the copied picture starts at k, the audio at t, and the gap between is filled with silence below
                args.AddRange(new[] { "-i", file.Url, "-map", "0:v:0?" });
                args.AddRange(new[] { "-map", audioStream != null ? $"0:{audioStream.Index}" : "0:a:0?" });
                args.AddRange(copyVideo
                    ? (IsHevc(videoStream?.Codec) ? new[] { "-c:v", "copy", "-tag:v", "hvc1" } : new[] { "-c:v", "copy" })   // hvc1 is the tag browsers expect for HEVC in MP4
                    : h != null ? h.VideoArgs
                    : new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p" });
                if (copyAudio)
                {
                    // Copied sound keeps its own timestamps; they are only moved so the stream starts at 0 (with a copied picture the file's own timestamps were kept, -copyts).
                    args.AddRange(new[] { "-c:a", "copy" });
                    if (copyVideo) args.AddRange(new[] { "-avoid_negative_ts", "make_zero" });
                }
                else
                {
                    // Some files have audio that begins a few seconds after the picture (or after the point a seek lands on). The browser ignores that gap in this kind of MP4
                    // and would play the sound too early, so the gap is filled with silence: the audio then really starts together with the picture.
                    // With a copied picture after a seek the picture starts at k (a keyframe before t) and the audio at t: the silence is padded from k, and all timestamps are then moved to start at 0.
                    var firstPts = seekCopy ? Math.Round(k!.Value * 48000).ToString("0", CultureInfo.InvariantCulture) : "0";
                    args.AddRange(new[] { "-af", $"aresample=async=1:first_pts={firstPts}" });
                    if (seekCopy) args.AddRange(new[] { "-ar", "48000", "-avoid_negative_ts", "make_zero" });
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ac", "2" });
                }
                // AC3 / E-AC3 sound can only be described in the MP4 header after ffmpeg has seen its first packets, so the header is written after the first fragment (a moment later than usual).
                var delayHeader = copyAudio && (audioStream!.Codec ?? "").ToLowerInvariant() is "ac3" or "eac3";
                args.AddRange(new[] { "-sn", "-dn", "-movflags", "frag_keyframe+empty_moov+default_base_moof" + (delayHeader ? "+delay_moov" : ""), "-f", "mp4", "pipe:1" });
                return args;
            }

            Task<string>? errText = null;
            Process StartFfmpeg(HwEncoderInfo? h)
            {
                var psi = new ProcessStartInfo(ffmpeg.Path!)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                };
                var args = BuildArgs(h);
                foreach (var a in args) psi.ArgumentList.Add(a);
                // Logged so a sync problem can be reproduced by hand: run this command with an output file instead of pipe:1 and play the file in a desktop player.
                log.LogInformation("Compatibility stream for file {FileId}: ffmpeg {Args}", fileId, string.Join(' ', args.Select(a => a.Contains(' ') ? '"' + a + '"' : a)));
                var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
                var label = h != null ? "ffmpeg (" + h.Name + "): " : "ffmpeg: ";
                errText = p.StandardError.ReadToEndAsync();
                _ = errText.ContinueWith(r => { if (r.IsCompletedSuccessfully && r.Result.Length > 0) log.LogWarning("{Label}{Error}", label, r.Result.Trim()); });
                return p;
            }

            ctx.Response.ContentType = "video/mp4";
            ctx.Response.Headers["Cache-Control"] = "no-store";

            proc = StartFfmpeg(hw);
            if (hw != null)
            {
                // The hardware encoder is only known to work if the machine says so; when a stream fails to produce anything with it (driver, unsupported source format),
                // the same stream is silently started again with the software encoder, so the viewer gets their video either way.
                var first = new byte[81920];
                var n = await proc.StandardOutput.BaseStream.ReadAsync(first, ct);
                if (n > 0)
                    await ctx.Response.Body.WriteAsync(first.AsMemory(0, n), ct);
                else
                {
                    var failed = hw;
                    string why;
                    try { using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct); wait.CancelAfter(TimeSpan.FromSeconds(3)); await errText!.WaitAsync(wait.Token); why = FirstLine(errText.Result); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { why = "no output"; }
                    log.LogWarning("Hardware encoder {Name} produced no video for file {FileId} ({Why}); trying the software encoder.", failed.Name, fileId, why);
                    try { if (!proc.HasExited) proc.Kill(true); } catch { }
                    proc.Dispose();
                    proc = null;

                    if (holdsSlot) { slot!.Release(); holdsSlot = false; }
                    slot = _encodeSlots;
                    if (!await AcquireAsync(slot, EncodeSlotWait))
                    {
                        ctx.Response.Headers["Retry-After"] = "5";
                        return Results.Problem("The server is already re-encoding as many streams as it can. Try again shortly.", statusCode: 503);
                    }
                    hw = null;
                    StreamReports.Put(s, fileId, copyVideo, pictureWhy, copyAudio, soundWhy, "CPU");
                    proc = StartFfmpeg(null);
                    n = await proc.StandardOutput.BaseStream.ReadAsync(first, ct);
                    if (n > 0)
                    {
                        // The software encoder worked where the hardware one did not: the fault is the hardware (not the source), so stop trying it for a while.
                        hardware.ReportFailure(failed, why);
                        await ctx.Response.Body.WriteAsync(first.AsMemory(0, n), ct);
                    }
                }
            }
            await proc.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, 81920, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.LogWarning("Remux failed for file {FileId}: {Message}", fileId, ex.Message);
            if (!ctx.Response.HasStarted) return Results.Problem("Could not start ffmpeg: " + ex.Message, statusCode: 500);
        }
        finally
        {
            if (holdsSlot) slot!.Release();   // free the slot first: a new request from the same viewer (seek, audio switch) must not be refused while the old process is still being killed
            var endedByItself = proc is { HasExited: true };
            int? code = null;
            try { if (endedByItself) code = proc!.ExitCode; } catch { }
            try { if (proc is { HasExited: false }) proc.Kill(true); } catch { /* already finished */ }
            proc?.Dispose();
            if (proc != null)
                log.LogInformation("Compatibility stream for file {FileId} ended after {Seconds:0.0} s: {How}.", fileId, started.Elapsed.TotalSeconds,
                    endedByItself ? "ffmpeg finished by itself (exit code " + code + ")" : "the viewer left or moved on, ffmpeg stopped");
        }
        return Results.Empty;
    }

    /// <summary>
    /// Remembers which compatibility stream each viewer (the random id the player sends as <c>s</c>, one per page view) has running for a movie,
    /// so starting a new one can stop the old one first. A request without an id behaves as before (nothing is replaced).
    /// </summary>
    internal static class ViewerStreams
    {
        /// <summary>How long a new stream waits for the one it replaces to let go of its ffmpeg process and slot.</summary>
        private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(3);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lease> Items = new();

        internal sealed class Lease : IDisposable
        {
            private readonly string _key;
            private readonly CancellationTokenSource _cts;
            internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Lease(string key, CancellationToken requestAborted) { _key = key; _cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted); }
            /// <summary>Cancelled when the request is aborted or the same viewer starts another stream.</summary>
            public CancellationToken Token => _cts.Token;
            internal void Replace() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } }
            public void Dispose()
            {
                Items.TryRemove(new KeyValuePair<string, Lease>(_key, this));
                Done.TrySetResult();
                // the token source is left to the garbage collector: a newer request may still be calling Replace() on it
            }
        }

        /// <summary>Registers a new stream for the viewer and movie, stops the previous one and waits (briefly) until it has released its resources. Null when there is no viewer id.</summary>
        internal static async Task<Lease?> StartAsync(string? viewer, int fileId, CancellationToken requestAborted)
        {
            if (string.IsNullOrWhiteSpace(viewer) || viewer.Length > 64) return null;
            var lease = new Lease(viewer + "|" + fileId, requestAborted);
            Lease? previous = null;
            Items.AddOrUpdate(viewer + "|" + fileId, lease, (_, old) => { previous = old; return lease; });
            if (previous != null)
            {
                previous.Replace();
                try { await Task.WhenAny(previous.Done.Task, Task.Delay(ReleaseWait, requestAborted)); }
                catch (OperationCanceledException) { /* this request was abandoned too; its own cleanup follows */ }
            }
            return lease;
        }

        /// <summary>Number of viewers with a running stream (for tests).</summary>
        internal static int Count => Items.Count;
    }

    /// <summary>The largest gap between the start of the picture and the start of the sound for which the sound is copied (a bit under what people notice).</summary>
    internal const double AudioSyncToleranceSeconds = 0.1;

    /// <summary>Sound a browser can play when it sits in an MP4 as it is (AAC, MP3, AC3, E-AC3; whether this particular browser can decode AC3 is the player's call).</summary>
    internal static bool CanCopyAudio(string? codec, int? channels)
        => channels is >= 1 and <= 8 && (codec ?? "").ToLowerInvariant() is "aac" or "mp3" or "ac3" or "eac3";

    /// <summary>
    /// Where the picture and the chosen sound really start in a copy (same seek as the stream will use): the first packet time of each, in seconds.
    /// A null means that stream gave no packet.
    /// </summary>
    private static async Task<(double? Video, double? Audio)> FirstPacketTimesAsync(string ffmpegPath, string url, double seek, int audioIndex, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "http,https,tcp,tls,crypto" };
        if (seek > 0) { args.Add("-ss"); args.Add(seek.ToString("0.###", CultureInfo.InvariantCulture)); }
        // -to is an absolute time here (the file's own timestamps are kept): the first 4 s after the seek point hold a first packet of each stream, even when the sound starts a little late.
        // (-frames cannot be used: ffmpeg then stops after the first stream that reaches its limit.)
        args.AddRange(new[] { "-copyts", "-i", url, "-map", "0:v:0", "-map", $"0:{audioIndex}", "-c", "copy", "-sn", "-dn",
                              "-to", (seek + 4).ToString("0.###", CultureInfo.InvariantCulture), "-f", "framecrc", "pipe:1" });
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        _ = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch (InvalidOperationException) { } throw; }
        TryReadFirstPacketTimes(await output, out var v, out var au);
        return (v, au);
    }

    /// <summary>Reads the first packet time of output stream 0 (picture) and 1 (sound) from ffmpeg's framecrc listing.</summary>
    internal static bool TryReadFirstPacketTimes(string framecrc, out double? video, out double? audio)
    {
        video = audio = null;
        var tb = new Dictionary<string, double>();
        foreach (var raw in framecrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("#tb ", StringComparison.Ordinal))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var stream = line[4..colon].Trim();
                var frac = line[(colon + 1)..].Trim().Split('/');
                if (frac.Length == 2 && double.TryParse(frac[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                    && double.TryParse(frac[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0) tb[stream] = num / den;
                continue;
            }
            if (line.StartsWith('#')) continue;
            var cols = line.Split(',', StringSplitOptions.TrimEntries);          // stream, dts, pts, duration, size, hash
            if (cols.Length < 3 || !tb.TryGetValue(cols[0], out var scale) || !long.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts)) continue;
            if (cols[0] == "0" && video == null) video = pts * scale;
            else if (cols[0] == "1" && audio == null) audio = pts * scale;
        }
        return video != null && audio != null;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(line) ? "no output" : line.Length > 200 ? line[..200] : line;
    }

    /// <summary>
    /// Where a converted stream that is asked to start at <c>t</c> really starts. When the picture is copied (not re-encoded) ffmpeg can only begin where its seek lands
    /// (a keyframe, which may be seconds before <c>t</c>, or even the start of the file when the file's index is sparse), and the audio must begin at exactly the same point
    /// or picture and sound drift apart. The landing point is found with the very same ffmpeg seek the stream will use, because other tools (ffprobe) can land elsewhere.
    /// If the seek lands far before <c>t</c>, the answer says to re-encode that stream: the re-encoded picture starts exactly at <c>t</c>.
    /// </summary>
    private static async Task<IResult> RemuxStartAsync(
        int fileId, double t, HttpContext ctx, IMediaLookup lookup, FfmpegTool ffmpeg,
        SourcePolicy policy, ILogger<MediaLog> log, string? video = null, string? hevc = null, int? audio = null, string? acopy = null)
    {
        ctx.Response.Headers["Cache-Control"] = "no-store";
        if (t <= 0 || double.IsNaN(t) || double.IsInfinity(t)) return Results.Json(new { start = 0.0, at = 0.0, encode = false });
        var file = await lookup.GetFileAsync(fileId, ctx.RequestAborted);
        if (file == null) return Results.NotFound();
        if (!policy.IsAllowed(file.Url)) return Results.StatusCode(403);

        var videoStream = file.Streams.FirstOrDefault(x => x.Kind == StreamKind.Video);
        var copying = CanCopyVideo(videoStream?.Codec, hevc == "1") && !_alwaysEncode && !string.Equals(video, "encode", StringComparison.OrdinalIgnoreCase);
        if (!copying || !ffmpeg.IsAvailable) return Results.Json(new { start = t, at = t, encode = false });   // a re-encoded picture can start exactly at t

        // Where the copy lands, whether that is a clean restart point and (when the sound may be copied too) where the sound starts do not depend on each other,
        // so they are looked up together. Each one opens the movie over the network, which is most of the wait; one after another they added up.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        Task<double?>? landingTask = null;
        Task<bool>? brokenTask = null;
        Task<(double? Video, double? Audio)>? syncTask = null;
        try
        {
            landingTask = FirstCopiedPacketAsync(ffmpeg.Path!, file.Url, t, cts.Token);
            brokenTask = LandsOnBrokenKeyframeAsync(ffmpeg.Path!, file.Url, t, cts.Token);
            var sound = acopy == "1" && _copyAudio ? PickAudio(file.Streams, audio) : null;
            if (sound != null && CanCopyAudio(sound.Codec, sound.Channels)) syncTask = FirstPacketTimesAsync(ffmpeg.Path!, file.Url, t, sound.Index, cts.Token);

            var first = await landingTask;
            if (first is { } landing)
            {
                landing = Math.Max(0, Math.Min(landing, t));
                // A copy that would start this far before the requested point shows the wrong part of the movie: re-encode instead (exact).
                if (t - landing > MaxCopySeekGapSeconds) return Results.Json(new { start = t, at = t, encode = true });
                // A seek that lands on a keyframe which is not a clean restart point (open GOP: pictures that reference frames before it) cannot be played by browsers: re-encode instead.
                if (await brokenTask) return Results.Json(new { start = t, at = t, encode = true });
                if (syncTask != null)
                {
                    try { SyncTimes.Put(fileId, t, sound!.Index, await syncTask); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { /* the stream measures it itself */ }
                }
                // The landing point depends on the exact seek value (indexes round their timestamps), so the stream is requested with this very same t.
                return Results.Json(new { start = landing, at = t, encode = false });   // the stream is requested with the same t, so it lands exactly here
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            log.LogWarning("Seek lookup for file {FileId} at {T}s failed: {Message}", fileId, t, ex.Message);
        }
        finally
        {
            cts.Cancel();   // a lookup whose answer is no longer needed (the answer is already decided, or the viewer jumped again) is stopped
            await Task.WhenAll(Quiet(landingTask), Quiet(brokenTask), Quiet(syncTask));
        }
        return Results.Json(new { start = t, at = t, encode = true });   // could not tell where a copy would start: the exact (re-encoded) way is safe
    }

    private static async Task Quiet(Task? task)
    {
        if (task == null) return;
        try { await task; } catch { /* its outcome no longer matters */ }
    }

    /// <summary>The audio track a stream uses: the one asked for, or (no choice) English when the file has an English track, otherwise the file's default.</summary>
    internal static StreamRow? PickAudio(IEnumerable<StreamRow> streams, int? audio)
    {
        var tracks = streams.Where(s => s.Kind == StreamKind.Audio).OrderBy(s => s.Index).ToList();
        if (audio.HasValue) return tracks.FirstOrDefault(s => s.Index == audio.Value);
        var pick = AudioPreference.Choose(tracks, s => s.Language, s => s.Title, s => s.IsDefault);
        return pick >= 0 ? tracks[pick] : null;
    }

    /// <summary>
    /// Where the picture and the sound start for a seek, measured by the seek lookup (/start) so the stream that follows a moment later does not have to measure it again.
    /// Short-lived and small: a value only helps the request that comes right after.
    /// </summary>
    /// <summary>What a viewer's current compatibility stream really does (copied or converted, and why), so the player can show the truth instead of the method that was asked for.</summary>
    internal sealed record StreamReport(bool PictureCopied, string PictureWhy, bool SoundCopied, string SoundWhy, string? Encoder, long Seq);
    internal static class StreamReports
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, StreamReport Report)> Items = new();
        private static long _seq;
        public static void Put(string? viewer, int fileId, bool pictureCopied, string pictureWhy, bool soundCopied, string soundWhy, string? encoder)
        {
            if (string.IsNullOrWhiteSpace(viewer) || viewer.Length > 64) return;
            var now = DateTime.UtcNow;
            if (Items.Count > 2000) foreach (var kv in Items) if (now - kv.Value.At > TimeSpan.FromMinutes(30)) Items.TryRemove(kv.Key, out _);
            Items[viewer + "|" + fileId] = (now, new StreamReport(pictureCopied, pictureWhy, soundCopied, soundWhy, encoder, Interlocked.Increment(ref _seq)));
        }
        public static StreamReport? Get(string? viewer, int fileId)
            => !string.IsNullOrWhiteSpace(viewer) && Items.TryGetValue(viewer + "|" + fileId, out var e) ? e.Report : null;
    }

    private static IResult RemuxReport(int fileId, string? s = null)
    {
        var r = StreamReports.Get(s, fileId);
        return r == null ? Results.NoContent() : Results.Json(new
        {
            picture = r.PictureCopied ? "copied" : "re-encoded", pictureWhy = r.PictureWhy,
            sound = r.SoundCopied ? "copied" : "converted", soundWhy = r.SoundWhy,
            encoder = r.Encoder, seq = r.Seq
        });
    }

    internal static class SyncTimes
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, (double? Video, double? Audio) Times)> Items = new();
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
        private static string Key(int fileId, double t, int audioIndex) => $"{fileId}|{t.ToString("0.###", CultureInfo.InvariantCulture)}|{audioIndex}";

        public static void Put(int fileId, double t, int audioIndex, (double? Video, double? Audio) times)
        {
            var now = DateTime.UtcNow;
            if (Items.Count > 64) foreach (var kv in Items) if (now - kv.Value.At > Lifetime) Items.TryRemove(kv.Key, out _);
            if (Items.Count > 256) Items.Clear();   // never grows without bound, whatever is requested
            Items[Key(fileId, t, audioIndex)] = (now, times);
        }

        public static bool TryGet(int fileId, double t, int audioIndex, out (double? Video, double? Audio) times)
        {
            times = default;
            if (!Items.TryGetValue(Key(fileId, t, audioIndex), out var e) || DateTime.UtcNow - e.At > Lifetime) return false;
            times = e.Times;
            return true;
        }
    }

    /// <summary>The time of the first picture ffmpeg copies when told to start at <paramref name="seek"/>, found with the same seek the real stream uses.</summary>
    private static async Task<double?> FirstCopiedPacketAsync(string ffmpegPath, string url, double seek, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        // -copyts keeps the file's own timestamps, so the first copied packet tells where the seek really landed.
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "http,https,tcp,tls,crypto",
                                  "-ss", seek.ToString("0.###", CultureInfo.InvariantCulture), "-copyts", "-i", url,
                                  "-map", "0:v:0", "-c:v", "copy", "-an", "-sn", "-dn", "-frames:v", "1", "-f", "framecrc", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        _ = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch (InvalidOperationException) { } throw; }
        return TryReadFirstPacketTime(await output, out var seconds) ? seconds : null;
    }

    private const double MaxCopySeekGapSeconds = 20;

    internal static bool IsHevc(string? codec) => codec is "hevc" or "h265" or "hvc1" or "hev1";

    /// <summary>Whether the picture can be copied into the converted stream as it is: H.264 always, HEVC only when the browser said it can show it.</summary>
    internal static bool CanCopyVideo(string? codec, bool browserShowsHevc) => codec is "h264" or "avc1" || (browserShowsHevc && IsHevc(codec));

    /// <summary>
    /// True when decoding a couple of seconds from the same seek point makes the decoder complain about missing reference pictures. That is what a copy that starts on an
    /// open-GOP keyframe looks like, and browsers refuse to play such a stream (or show garbage), so those seeks are re-encoded instead.
    /// </summary>
    private static async Task<bool> LandsOnBrokenKeyframeAsync(string ffmpegPath, string url, double seek, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "warning", "-nostdin", "-protocol_whitelist", "http,https,tcp,tls,crypto",
                                  "-ss", seek.ToString("0.###", CultureInfo.InvariantCulture), "-noaccurate_seek", "-i", url,
                                  "-map", "0:v:0", "-t", "2", "-an", "-sn", "-dn", "-f", "null", "-" })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        _ = proc.StandardOutput.ReadToEndAsync();
        var err = proc.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch (InvalidOperationException) { } throw; }
        return HasMissingReferenceWarning(await err);
    }

    internal static bool HasMissingReferenceWarning(string stderr) =>
        stderr.Contains("mmco: unref short failure", StringComparison.Ordinal)
        || stderr.Contains("reference picture missing", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("co located POCs unavailable", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Missing reference picture", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("decode_slice_header error", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Could not find ref with POC", StringComparison.OrdinalIgnoreCase);   // HEVC

    /// <summary>Reads the presentation time (seconds) of the first packet from ffmpeg's <c>framecrc</c> output.</summary>
    internal static bool TryReadFirstPacketTime(string framecrc, out double seconds)
    {
        seconds = 0;
        double tb = 0;
        foreach (var raw in framecrc.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("#tb 0:", StringComparison.Ordinal))
            {
                var frac = line["#tb 0:".Length..].Trim().Split('/');
                if (frac.Length == 2 && double.TryParse(frac[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                    && double.TryParse(frac[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0) tb = num / den;
                continue;
            }
            if (line.StartsWith('#') || tb <= 0) continue;
            var cols = line.Split(',', StringSplitOptions.TrimEntries);          // stream, dts, pts, duration, size, hash
            if (cols.Length >= 3 && cols[0] == "0" && long.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts))
            { seconds = pts * tb; return true; }
        }
        return false;
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
