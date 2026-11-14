using System.Text.Json.Serialization;

namespace StreamApp.Infrastructure.Metadata;

public sealed class TmdbSearchResponse
{
    [JsonPropertyName("results")] public List<TmdbSearchItem> Results { get; set; } = new();
}

public sealed class TmdbSearchItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("popularity")] public double Popularity { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }

    [JsonIgnore] public int? Year => TmdbDates.YearOf(ReleaseDate);
}

public sealed class TmdbGenre
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class TmdbNamed
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class TmdbCastItem
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("character")] public string? Character { get; set; }
    [JsonPropertyName("profile_path")] public string? ProfilePath { get; set; }
    [JsonPropertyName("order")] public int Order { get; set; }
}

public sealed class TmdbCrewItem
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("job")] public string? Job { get; set; }
}

public sealed class TmdbCredits
{
    [JsonPropertyName("cast")] public List<TmdbCastItem> Cast { get; set; } = new();
    [JsonPropertyName("crew")] public List<TmdbCrewItem> Crew { get; set; } = new();
}

public sealed class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")] public string? ImdbId { get; set; }
}

public sealed class TmdbMovieDetails
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("imdb_id")] public string? ImdbId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("tagline")] public string? Tagline { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("runtime")] public int? Runtime { get; set; }
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("popularity")] public double Popularity { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
    [JsonPropertyName("genres")] public List<TmdbGenre> Genres { get; set; } = new();
    [JsonPropertyName("production_companies")] public List<TmdbNamed> ProductionCompanies { get; set; } = new();
    [JsonPropertyName("production_countries")] public List<TmdbNamed> ProductionCountries { get; set; } = new();
    [JsonPropertyName("credits")] public TmdbCredits? Credits { get; set; }
    [JsonPropertyName("external_ids")] public TmdbExternalIds? ExternalIds { get; set; }

    [JsonIgnore] public int? Year => TmdbDates.YearOf(ReleaseDate);
}

public static class TmdbDates
{
    public static int? YearOf(string? date) =>
        !string.IsNullOrEmpty(date) && date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;

    public static DateTime? Parse(string? date) =>
        DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
