using StreamApp.Data;
using StreamApp.Models;
using StreamApp.Services;

namespace StreamApp.Pages.Admin.Movies;

public class AddModel : AddContentModel
{
    public AddModel(AppDbContext db, CrawlerService crawler) : base(db, crawler) { }
    public override CatalogType Kind => CatalogType.Movie;
}
