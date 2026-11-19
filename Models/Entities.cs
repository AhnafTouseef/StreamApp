namespace StreamApp.Models;

public enum ScanStrategy { Auto, YearBased, Flat, RecursiveDiscovery, ManualFull }
public enum ScanRunStatus { Running, Succeeded, Partial, Failed, Interrupted, Cancelled }
public enum MatchStatus { Unmatched, Matched, Ambiguous, Manual }
public enum StreamKind { Video, Audio, Subtitle, Other }
/// <summary>What an administrator declares for a source. Automatic inspects the directory structure at scan time; Movies and Series override that.</summary>
public enum SourceContentType { Automatic, Movies, Series }
/// <summary>What a catalog entry is. Stored on the entry itself, so cards and pages never depend on category names.</summary>
public enum CatalogType { Movie, Series }

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public int? ParentCategoryId { get; set; }
    public Category? Parent { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Category> Children { get; set; } = new List<Category>();
    public ICollection<Source> Sources { get; set; } = new List<Source>();
    public ICollection<Movie> Movies { get; set; } = new List<Movie>();
}

public class Source
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public string Name { get; set; } = "";
    public string RootUrl { get; set; } = "";
    public ScanStrategy ScanStrategy { get; set; } = ScanStrategy.Auto;
    public int CooldownHours { get; set; } = 12;
    public DateTime? LastScanAttempt { get; set; }
    public DateTime? LastSuccessfulScan { get; set; }
    public ScanRunStatus? LastScanStatus { get; set; }
    public string? LastError { get; set; }
    public bool IsEnabled { get; set; } = true;
    /// <summary>Newline-separated folder names (wildcards * and ? allowed). When set, only these top-level folders (plus year folders for year-based sources) are crawled.</summary>
    public string? IncludedFolders { get; set; }
    /// <summary>Newline-separated folder names (wildcards allowed) that are never crawled, at any depth.</summary>
    public string? ExcludedFolders { get; set; }
    /// <summary>Automatic (default): the crawler inspects the folder structure. Movies / Series: manual override.</summary>
    public SourceContentType ContentType { get; set; } = SourceContentType.Automatic;

    public ICollection<Movie> Movies { get; set; } = new List<Movie>();
    public ICollection<ScanHistory> ScanHistories { get; set; } = new List<ScanHistory>();
    public ICollection<SourceDirectory> Directories { get; set; } = new List<SourceDirectory>();
}

/// <summary>Remembers which year/collection directories of a source have been listed, for incremental scans.</summary>
public class SourceDirectory
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public Source Source { get; set; } = null!;
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Year { get; set; }
    public DateTime? LastScannedAt { get; set; }
    public int ItemCount { get; set; }
}

/// <summary>
/// The catalog entry shown on cards and in categories, search and genres. For a movie it is the movie; for a TV series it is the series
/// (<see cref="ContentType"/> = Series) and <see cref="Series"/> holds the series-specific data with its seasons and episodes.
/// </summary>
public class Movie
{
    public int Id { get; set; }
    public CatalogType ContentType { get; set; } = CatalogType.Movie;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public int SourceId { get; set; }
    public Source Source { get; set; } = null!;

    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public string FolderName { get; set; } = "";
    public string SourceFolderUrl { get; set; } = "";
    public string? PosterUrl { get; set; }
    public string? LocalPosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? Overview { get; set; }
    public string? Tagline { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public int? ReleaseYear { get; set; }
    public int? Runtime { get; set; }
    public int? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public double? TmdbRating { get; set; }
    public int? TmdbVoteCount { get; set; }
    public double? Popularity { get; set; }
    public double? ImdbRating { get; set; }
    public string? Director { get; set; }
    public string? CastJson { get; set; }
    public string? ProductionInfo { get; set; }
    public string? Quality { get; set; }
    public string? Tags { get; set; }

    public MatchStatus MatchStatus { get; set; } = MatchStatus.Unmatched;
    public string? MatchNote { get; set; }
    public DateTime? MetadataFetchedAt { get; set; }
    public DateTime? FolderLastModified { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastDiscoveredAt { get; set; } = DateTime.UtcNow;

    public ICollection<MovieGenre> MovieGenres { get; set; } = new List<MovieGenre>();
    public ICollection<MediaFile> MediaFiles { get; set; } = new List<MediaFile>();
    public ICollection<Subtitle> Subtitles { get; set; } = new List<Subtitle>();
    public Series? Series { get; set; }
}

/// <summary>Series-specific data of a catalog entry (Series → Seasons → Episodes → MediaFiles). Shares its primary key with the entry's <see cref="Movie.Id"/>.</summary>
public class Series
{
    public int Id { get; set; }
    public Movie Catalog { get; set; } = null!;
    public string? Status { get; set; }
    public string? Networks { get; set; }
    public DateTime? LastAirDate { get; set; }
    public int? TmdbSeasonCount { get; set; }
    public int? TmdbEpisodeCount { get; set; }
    /// <summary>Newest change seen in the series' folders; routine scans keep re-checking recently active series.</summary>
    public DateTime? LastActivityAt { get; set; }
    public ICollection<Season> Seasons { get; set; } = new List<Season>();
}

public class Season
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public Series Series { get; set; } = null!;
    /// <summary>0 = Specials.</summary>
    public int Number { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public DateTime? AirDate { get; set; }
    public int? TmdbSeasonId { get; set; }
    public string? FolderName { get; set; }
    public string? FolderUrl { get; set; }
    public DateTime? FolderLastModified { get; set; }
    public ICollection<Episode> Episodes { get; set; } = new List<Episode>();
}

public class Episode
{
    public int Id { get; set; }
    public int SeasonId { get; set; }
    public Season Season { get; set; } = null!;
    public int Number { get; set; }
    /// <summary>Last episode number of a file that holds several episodes (S01E01E02), otherwise null.</summary>
    public int? EndNumber { get; set; }
    public string? Title { get; set; }
    public string? Overview { get; set; }
    public DateTime? AirDate { get; set; }
    public int? Runtime { get; set; }
    public string? StillUrl { get; set; }
    public double? TmdbRating { get; set; }
    /// <summary>True when the number was assigned by sorting file names because the name carried none.</summary>
    public bool NumberingInferred { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<MediaFile> MediaFiles { get; set; } = new List<MediaFile>();
    public ICollection<Subtitle> Subtitles { get; set; } = new List<Subtitle>();
}

public class Genre
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public int? TmdbGenreId { get; set; }
    public ICollection<MovieGenre> MovieGenres { get; set; } = new List<MovieGenre>();
}

public class MovieGenre
{
    public int MovieId { get; set; }
    public Movie Movie { get; set; } = null!;
    public int GenreId { get; set; }
    public Genre Genre { get; set; } = null!;
}

public class MediaFile
{
    public int Id { get; set; }
    /// <summary>The catalog entry. For a series episode this is the series' entry, so every existing lookup by movie keeps working.</summary>
    public int MovieId { get; set; }
    public Movie Movie { get; set; } = null!;
    public int? EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    public string FileName { get; set; } = "";
    public string FileUrl { get; set; } = "";
    public string Extension { get; set; } = "";
    public long? FileSize { get; set; }
    public double? Duration { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? Resolution { get; set; }
    public DateTime? LastModified { get; set; }
    public string? FileFingerprint { get; set; }
    public DateTime? InspectedAt { get; set; }
    public string? InspectionError { get; set; }
    public ICollection<MediaStream> Streams { get; set; } = new List<MediaStream>();
}

public class MediaStream
{
    public int Id { get; set; }
    public int MediaFileId { get; set; }
    public MediaFile MediaFile { get; set; } = null!;
    public int StreamIndex { get; set; }
    public StreamKind StreamType { get; set; }
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
    public int? Channels { get; set; }
}

public class Subtitle
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    public Movie Movie { get; set; } = null!;
    public int? EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    public string FileName { get; set; } = "";
    public string FileUrl { get; set; } = "";
    public string? Language { get; set; }
    public string Format { get; set; } = "";
}

public class ScanHistory
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public Source Source { get; set; } = null!;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public ScanRunStatus Status { get; set; } = ScanRunStatus.Running;
    public bool Deep { get; set; }
    public string Trigger { get; set; } = "";
    public int DiscoveredCount { get; set; }
    public int AddedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int FailedCount { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class CastMember
{
    public string Name { get; set; } = "";
    public string? Character { get; set; }
    public string? Photo { get; set; }
}
