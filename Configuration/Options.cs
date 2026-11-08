namespace MovieHub.Configuration;

public sealed class TmdbOptions
{
    public const string Section = "Tmdb";
    /// <summary>TMDB v3 API key, or a v4 "API Read Access Token" (JWT, sent as a Bearer token).</summary>
    public string ApiKey { get; set; } = "";
    public string Language { get; set; } = "en-US";
    public string ApiBaseUrl { get; set; } = "https://api.themoviedb.org/3";
    public string ImageBaseUrl { get; set; } = "https://image.tmdb.org/t/p";
    public int MaxConcurrency { get; set; } = 3;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) && !ApiKey.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);
}

public sealed class OmdbOptions
{
    public const string Section = "Omdb";
    /// <summary>Optional. When set, IMDb ratings are fetched through the official OMDb API (no scraping).</summary>
    public string ApiKey { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class CrawlerOptions
{
    public const string Section = "Crawler";
    public int DefaultCooldownHours { get; set; } = 12;
    public int MaxConcurrentScans { get; set; } = 2;
    public int MaxConcurrentFolderTasks { get; set; } = 3;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxRecursionDepth { get; set; } = 6;
    public int MaxRedirects { get; set; } = 3;
    public int FailureBackoffMinutes { get; set; } = 60;
    /// <summary>
    /// Routine scans of sources without year folders (strategy Flat: the root holds the movie folders directly) only look at folders whose modified date
    /// is within this many months; older ones are skipped. The first scan of a source and every Deep scan ignore the window. 0 switches the window off.
    /// </summary>
    public int RecentWindowMonths { get; set; } = 24;
    public int DelayBetweenRequestsMs { get; set; } = 40;
    public int MaxConcurrentRequests { get; set; } = 4;
    /// <summary>false (default): the TMDB poster is used when the movie is matched, the poster file inside the ISP folder is the fallback. true: the folder poster always wins.</summary>
    public bool PreferFolderPoster { get; set; } = false;
    public string UserAgent { get; set; } = "MovieHub/1.0 (local catalog crawler)";
    public string[] VideoExtensions { get; set; } = { ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".webm" };
    public string[] SubtitleExtensions { get; set; } = { ".srt", ".vtt", ".ass", ".ssa" };
    public string[] ImageExtensions { get; set; } = { ".jpg", ".jpeg", ".png", ".webp" };
}

public sealed class MediaOptions
{
    public const string Section = "Media";
    /// <summary>
    /// Leave empty (recommended): ffprobe/ffmpeg are looked up in the project's own tools folder first, then on PATH, so the project
    /// can be copied to another computer as one folder. A value here is used exclusively (no fallback) and may be a full path.
    /// </summary>
    public string FfprobePath { get; set; } = "";
    /// <summary>Same rule as <see cref="FfprobePath"/>. ffmpeg enables audio-track switching and MKV/HEVC/AC3 playback.</summary>
    public string FfmpegPath { get; set; } = "";
    /// <summary>Folder (relative to the project folder) that holds ffmpeg and ffprobe.</summary>
    public string ToolsDirectory { get; set; } = "tools";
    /// <summary>Where the admin "Download FFmpeg" button fetches a Windows build from (a zip that contains ffmpeg.exe and ffprobe.exe).</summary>
    public string FfmpegDownloadUrl { get; set; } = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public int ProbeTimeoutSeconds { get; set; } = 90;
    public int MaxConcurrentProbes { get; set; } = 2;
    public int MaxFilesProbedPerMovie { get; set; } = 6;
    public int MaxRemuxSessions { get; set; } = 2;
    /// <summary>Compatibility mode always re-encodes the picture instead of copying it. Slower and uses CPU, but removes everything that depends on the file's keyframes and timestamps; use it to find out whether a sync problem comes from copying.</summary>
    public bool AlwaysEncodeVideo { get; set; } = false;
    /// <summary>
    /// Subtitles inside a video are extracted to WebVTT once and handed to the browser, which keeps them in its own storage (IndexedDB). The server only holds the
    /// extracted files for a short moment, in this folder (empty = the operating system's temp folder), and deletes them after <see cref="SubtitleStagingMinutes"/>.
    /// </summary>
    public string SubtitleStagingDirectory { get; set; } = "";
    public int SubtitleStagingMinutes { get; set; } = 15;
    /// <summary>How many files may have their embedded subtitles extracted at the same time (each extraction reads the whole video once).</summary>
    public int MaxSubtitleExtractions { get; set; } = 1;
    public int SubtitleExtractTimeoutMinutes { get; set; } = 30;
}

public sealed class AdminOptions
{
    public const string Section = "Admin";
    public string Username { get; set; } = "admin";
    /// <summary>PBKDF2 hash produced by: dotnet run -- hash-password "your password"</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>Plain-text alternative for local development only. Prefer PasswordHash.</summary>
    public string Password { get; set; } = "";
    public int SessionHours { get; set; } = 8;
    public bool IsConfigured => !string.IsNullOrEmpty(PasswordHash) || !string.IsNullOrEmpty(Password);
}

public sealed class SeedOptions
{
    public const string Section = "Seed";
    public List<SeedSource> Sources { get; set; } = new();
}

public sealed class SeedSource
{
    public string Name { get; set; } = "";
    public string CategorySlug { get; set; } = "";
    public string RootUrl { get; set; } = "";
    public string Strategy { get; set; } = "Auto";
    public int? CooldownHours { get; set; }
    public string? IncludedFolders { get; set; }
    public string? ExcludedFolders { get; set; }
    public bool Enabled { get; set; } = true;
}
