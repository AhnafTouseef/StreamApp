using StreamApp.Data;
using StreamApp.Models;

namespace StreamApp.Pages.Admin.TvSeries;

/// <summary>Admin list of TV series only.</summary>
public class IndexModel : ContentIndexModel
{
    public IndexModel(AppDbContext db) : base(db) { }
    public override CatalogType Kind => CatalogType.Series;
}
