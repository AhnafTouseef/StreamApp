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

    public async Task<IActionResult> OnGetAsync(string slug, string? q, int? year, string? sort, [FromQuery(Name = "page")] int page = 1)  // explicit: Razor Pages reserves the route value "page", so an unannotated parameter never sees ?page=N
    {
        var ct = HttpContext.RequestAborted;
        var genre = await _movies.GetGenreBySlugAsync(slug, ct);
        if (genre == null) return NotFound();
        Genre = genre;

        Browse = new BrowseViewModel
        {
            BasePath = $"/genres/{genre.Slug}",
            Result = await _movies.BrowseAsync(new BrowseRequest { GenreSlug = genre.Slug, Query = q, Year = year, Sort = sort ?? "popular", Page = page }, ct),
            Facets = await _movies.GetFacetsAsync(null, ct),
            Query = q, Genre = genre.Slug, Year = year, Sort = sort ?? "popular",
            QueryPlaceholder = $"Search {genre.Name} movies",
            EmptyTitle = "No movies", EmptyMessage = "No movies in this genre match the filters."
        };
        Browse.Facets.Genres.Clear(); // the genre is fixed on this page
        return Page();
    }
}
