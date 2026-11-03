using Microsoft.AspNetCore.Mvc.RazorPages;
using MovieHub.Models;
using MovieHub.Services;

namespace MovieHub.Pages;

public class IndexModel : PageModel
{
    private readonly MovieService _movies;
    public IndexModel(MovieService movies) => _movies = movies;
    public HomeDto Home { get; private set; } = new();

    public async Task OnGetAsync() => Home = await _movies.GetHomeAsync(HttpContext.RequestAborted);
}
