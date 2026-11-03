namespace MovieHub.Models;

public enum ScanStrategy { Auto, YearBased, Flat, RecursiveDiscovery, ManualFull }
public enum ScanRunStatus { Running, Succeeded, Partial, Failed, Interrupted, Cancelled }
public enum MatchStatus { Unmatched, Matched, Ambiguous, Manual }
public enum StreamKind { Video, Audio, Subtitle, Other }

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

public class Movie
{
    public int Id { get; set; }
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
    public int MovieId { get; set; }
    public Movie Movie { get; set; } = null!;
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
