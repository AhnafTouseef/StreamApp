using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Movies;

public class DetailsModel : PageModel
{
    private readonly MovieService _movies;
    public DetailsModel(MovieService movies) => _movies = movies;

    public Movie Movie { get; private set; } = null!;
    public List<CastMember> Cast { get; private set; } = new();
    public List<MovieCardDto> Similar { get; private set; } = new();
    public List<MovieCopyDto> OtherCopies { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var ct = HttpContext.RequestAborted;
        var movie = await _movies.GetMovieAsync(id, ct);
        if (movie == null) return NotFound();
        Movie = movie;

        if (!string.IsNullOrEmpty(movie.CastJson))
        {
            try { Cast = JsonSerializer.Deserialize<List<CastMember>>(movie.CastJson) ?? new(); }
            catch (JsonException) { Cast = new(); }
        }
        Similar = await _movies.GetSimilarAsync(movie, 18, ct);
        OtherCopies = await _movies.GetOtherCopiesAsync(movie, ct);
        return Page();
    }
}
