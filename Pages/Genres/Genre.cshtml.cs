using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Genres;

public class GenreModel : PageModel
{
    private readonly MovieService _movies;
    public GenreModel(MovieService movies) => _movies = movies;

    public Genre Genre { get; private set; } = null!;
    public BrowseViewModel Browse { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(string slug, string? q, int? year, string? sort, int page = 1)
    {
        var ct = HttpContext.RequestAborted;
        var genre = await _movies.GetGenreBySlugAsync(slug, ct);
        if (genre == null) return NotFound();
        Genre = genre;

        Browse = new BrowseViewModel
        {
            BasePath = $"/genres/{genre.Slug}",
            Result = await _movies.BrowseAsync(new BrowseRequest { GenreSlug = genre.Slug, Query = q, Year = year, Sort = sort ?? "recent", Page = page }, ct),
            Facets = await _movies.GetFacetsAsync(null, ct),
            Query = q, Genre = genre.Slug, Year = year, Sort = sort ?? "recent",
            QueryPlaceholder = $"Search {genre.Name} movies",
            EmptyTitle = "No movies", EmptyMessage = "No movies in this genre match the filters."
        };
        Browse.Facets.Genres.Clear(); // the genre is fixed on this page
        return Page();
    }
}
