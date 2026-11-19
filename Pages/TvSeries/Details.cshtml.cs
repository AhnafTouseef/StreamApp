using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StreamApp.Models;
using StreamApp.Services;

namespace StreamApp.Pages.TvSeries;

public class DetailsModel : PageModel
{
    private readonly MovieService _movies;
    public DetailsModel(MovieService movies) => _movies = movies;

    public SeriesView View { get; private set; } = null!;
    public Movie Entry => View.Entry;
    public List<CastMember> Cast { get; private set; } = new();
    public List<MovieCardDto> Similar { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var ct = HttpContext.RequestAborted;
        var view = await _movies.GetSeriesViewAsync(id, ct);
        if (view == null)
            return await _movies.GetContentTypeAsync(id, ct) == CatalogType.Movie ? RedirectToPage("/Movies/Details", new { id }) : NotFound();
        if (!User.Identity!.IsAuthenticated && !await _movies.IsVisibleAsync(id, ct)) return NotFound(); // hidden category (admins can still preview)
        View = view;

        if (!string.IsNullOrEmpty(Entry.CastJson))
        {
            try { Cast = JsonSerializer.Deserialize<List<CastMember>>(Entry.CastJson) ?? new(); }
            catch (JsonException) { Cast = new(); }
        }
        Similar = await _movies.GetSimilarAsync(Entry, 18, ct);
        return Page();
    }
}
