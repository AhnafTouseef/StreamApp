using StreamApp.Data;
using StreamApp.Infrastructure.Media;
using StreamApp.Infrastructure.Metadata;
using StreamApp.Models;

namespace StreamApp.Pages.Admin.TvSeries;

/// <summary>Edit page of a series. Same behaviour as the movie page (TMDB search/apply/refetch, re-inspect, delete), reached from the Series list.</summary>
public class EditModel : Movies.EditModel
{
    public EditModel(AppDbContext db, MetadataService metadata, MediaInspectionService inspect, ILogger<Movies.EditModel> log) : base(db, metadata, inspect, log) { }
    protected override CatalogType Section => CatalogType.Series;
}
