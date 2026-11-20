using StreamApp.Data;
using StreamApp.Models;
using StreamApp.Services;

namespace StreamApp.Pages.Admin.TvSeries;

public class AddModel : AddContentModel
{
    public AddModel(AppDbContext db, CrawlerService crawler) : base(db, crawler) { }
    public override CatalogType Kind => CatalogType.Series;
}
