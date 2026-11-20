using StreamApp.Data;
using StreamApp.Models;

namespace StreamApp.Pages.Admin.Movies;

/// <summary>Admin list of movies only.</summary>
public class IndexModel : ContentIndexModel
{
    public IndexModel(AppDbContext db) : base(db) { }
    public override CatalogType Kind => CatalogType.Movie;
}
