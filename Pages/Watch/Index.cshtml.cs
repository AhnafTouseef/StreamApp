using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;
using MovieHub.Infrastructure.Crawler;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Watch;

public class IndexModel : PageModel
{
    private readonly MovieService _movies;
    private readonly MediaOptions _media;
    private readonly SourcePolicy _policy;

    public IndexModel(MovieService movies, IOptions<MediaOptions> media, SourcePolicy policy)
    {
        _movies = movies; _media = media.Value; _policy = policy;
    }

    public Movie Movie { get; private set; } = null!;
    public MediaFile? Current { get; private set; }
    public string ConfigJson { get; private set; } = "{}";

    public async Task<IActionResult> OnGetAsync(int id, int? file)
    {
        var movie = await _movies.GetMovieAsync(id, HttpContext.RequestAborted);
        if (movie == null) return NotFound();
        Movie = movie;

        var files = movie.MediaFiles.OrderBy(f => f.FileName).ToList();
        Current = (file.HasValue ? files.FirstOrDefault(f => f.Id == file.Value) : null)
               ?? files.OrderByDescending(f => f.Extension is ".mp4" or ".m4v" or ".webm").ThenBy(f => f.FileName).FirstOrDefault();
        if (Current == null) return Page();

        // Only a URL that is still under an approved source is handed to the browser.
        var direct = _policy.IsAllowed(Current.FileUrl) ? Current.FileUrl : null;

        var audio = Current.Streams.Where(s => s.StreamType == StreamKind.Audio).OrderBy(s => s.StreamIndex)
            .Select(s => new { index = s.StreamIndex, lang = s.Language, title = s.Title, codec = s.Codec, channels = s.Channels, isDefault = s.IsDefault }).ToList();
        var embeddedSubs = Current.Streams.Count(s => s.StreamType == StreamKind.Subtitle);
        var subs = movie.Subtitles.OrderBy(s => s.Language).ThenBy(s => s.FileName)
            .Select(s => new
            {
                id = s.Id, lang = s.Language, file = s.FileName, format = s.Format,
                supported = s.Format is "srt" or "vtt" or "ass" or "ssa"
            }).ToList();

        ConfigJson = JsonSerializer.Serialize(new
        {
            fileId = Current.Id,
            directUrl = direct,
            proxyUrl = $"/media/stream/{Current.Id}",
            remuxUrl = $"/media/remux/{Current.Id}",
            remuxEnabled = _media.RemuxEnabled,
            extension = Current.Extension,
            duration = Current.Duration,
            videoCodec = Current.VideoCodec,
            audioCodec = Current.AudioCodec,
            audio,
            subtitles = subs,
            embeddedSubtitles = embeddedSubs
        });
        return Page();
    }
}
