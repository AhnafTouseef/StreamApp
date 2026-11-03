using Microsoft.AspNetCore.Mvc.RazorPages;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages.Genres;

public class IndexModel : PageModel
{
    private readonly MovieService _movies;
    public IndexModel(MovieService movies) => _movies = movies;
    public List<GenreFacet> Genres { get; private set; } = new();
    public async Task OnGetAsync() => Genres = await _movies.GetGenresAsync(HttpContext.RequestAborted);
}
