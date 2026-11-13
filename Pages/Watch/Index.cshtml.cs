using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;
using MovieHub.Infrastructure.Crawler;
using MovieHub.Infrastructure.Media;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Watch;

public class IndexModel : PageModel
{
    private readonly MovieService _movies;
    private readonly FfmpegTool _ffmpeg;
    private readonly SourcePolicy _policy;
    private readonly MediaOptions _media;
    private readonly HardwareEncoder _hw;
    private readonly SiteSettingsService _site;

    public IndexModel(MovieService movies, FfmpegTool ffmpeg, SourcePolicy policy, IOptions<MediaOptions> media, HardwareEncoder hw, SiteSettingsService site)
    {
        _movies = movies; _ffmpeg = ffmpeg; _policy = policy; _media = media.Value; _hw = hw; _site = site;
    }

    public Movie Movie { get; private set; } = null!;
    public MediaFile? Current { get; private set; }
    public string ConfigJson { get; private set; } = "{}";

    public async Task<IActionResult> OnGetAsync(int id, int? file)
    {
        var movie = await _movies.GetMovieAsync(id, HttpContext.RequestAborted);
        if (movie == null) return NotFound();
        if (!User.Identity!.IsAuthenticated && !await _movies.IsVisibleAsync(id, HttpContext.RequestAborted)) return NotFound(); // hidden category (admins can still preview)
        Movie = movie;

        var files = movie.MediaFiles.OrderBy(f => f.FileName).ToList();
        Current = (file.HasValue ? files.FirstOrDefault(f => f.Id == file.Value) : null)
               ?? files.OrderByDescending(f => f.Extension is ".mp4" or ".m4v" or ".webm").ThenBy(f => f.FileName).FirstOrDefault();
        if (Current == null) return Page();

        // Only a URL that is still under an approved source is handed to the browser.
        var direct = _policy.IsAllowed(Current.FileUrl) ? Current.FileUrl : null;

        var audio = Current.Streams.Where(s => s.StreamType == StreamKind.Audio).OrderBy(s => s.StreamIndex)
            .Select(s => new { index = s.StreamIndex, lang = s.Language, title = s.Title, codec = s.Codec, channels = s.Channels, isDefault = s.IsDefault }).ToList();
        // Multi-audio files start with English when there is an English track; the player switches to it before playback if the file's own default differs.
        var pick = AudioPreference.Choose(audio, a => a.lang, a => a.title, a => a.isDefault);
        var fileDefault = audio.FindIndex(a => a.isDefault);
        if (fileDefault < 0 && audio.Count > 0) fileDefault = 0;
        int? preferredAudio = pick >= 0 ? audio[pick].index : null;
        int? defaultAudio = fileDefault >= 0 ? audio[fileDefault].index : null;
        var embeddedSubs = Current.Streams.Count(s => s.StreamType == StreamKind.Subtitle);
        var subs = movie.Subtitles.OrderBy(s => s.Language).ThenBy(s => s.FileName)
            .Select(s => new
            {
                id = s.Id, lang = s.Language, file = s.FileName, format = s.Format,
                supported = s.Format is "srt" or "vtt" or "ass" or "ssa"
            }).ToList();

        var view = _site.Current.ViewFor(User.Identity!.IsAuthenticated);   // what this visitor may see (Admin > Site)
        ConfigJson = JsonSerializer.Serialize(new
        {
            ui = new { hideTech = view.HideTechnical, hideHints = view.HideHints, hideCompat = view.HideCompat },
            fileId = Current.Id,
            fingerprint = Current.FileFingerprint ?? "",
            directUrl = direct,
            proxyUrl = $"/media/stream/{Current.Id}",
            remuxUrl = $"/media/remux/{Current.Id}",
            remuxEnabled = _ffmpeg.IsAvailable,
            alwaysEncode = _media.AlwaysEncodeVideo,
            copyAudio = _media.CopyAudio,
            hwEncoder = _hw.Current?.Name,   // e.g. "Intel Quick Sync" when re-encoding runs on the graphics hardware, otherwise null
            inspected = Current.InspectedAt != null && Current.InspectionError == null, // a failed ffprobe run also sets InspectedAt, but it tells us nothing about the tracks
            inspectionError = Current.InspectionError,
            extension = Current.Extension,
            duration = Current.Duration,
            videoCodec = Current.VideoCodec,
            audioCodec = Current.AudioCodec,
            audio,
            preferredAudio,
            defaultAudio,
            subtitles = subs,
            embeddedSubtitles = embeddedSubs
        });
        return Page();
    }
}
