using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;
using MovieHub.Data;
using MovieHub.Infrastructure.Crawler;
using MovieHub.Models;

namespace MovieHub.Infrastructure.Metadata;

public sealed record MatchOutcome(MatchStatus Status, int? TmdbId, string Note);

/// <summary>
/// Matches catalog entries to TMDB and copies metadata onto them. A TMDB id is stored only for a confident match
/// (exact title and year); anything else stays <see cref="MatchStatus.Ambiguous"/> or <see cref="MatchStatus.Unmatched"/>
/// so an administrator can correct it. Nothing is ever associated blindly with the first search hit.
/// </summary>
public sealed class MetadataService
{
    private readonly TmdbClient _tmdb;
    private readonly OmdbClient _omdb;
    private readonly GenreResolver _genres;
    private readonly CrawlerOptions _crawler;
    private readonly ILogger<MetadataService> _log;

    public MetadataService(TmdbClient tmdb, OmdbClient omdb, GenreResolver genres, IOptions<CrawlerOptions> crawler, ILogger<MetadataService> log)
    {
        _tmdb = tmdb;
        _omdb = omdb;
        _genres = genres;
        _crawler = crawler.Value;
        _log = log;
    }

    public bool IsConfigured => _tmdb.IsConfigured;
    public TmdbClient Tmdb => _tmdb;

    // ------------------------------------------------------------------ matching

    public sealed record Candidate(TmdbSearchItem Item, int Score, bool ExactTitle, int YearDelta);

    public static List<Candidate> Rank(IEnumerable<TmdbSearchItem> results, string title, int? year)
    {
        var wanted = FolderNameParser.Normalize(title);
        var list = new List<Candidate>();
        foreach (var r in results)
        {
            var t1 = FolderNameParser.Normalize(r.Title);
            var t2 = FolderNameParser.Normalize(r.OriginalTitle);
            var exact = wanted.Length > 0 && (t1 == wanted || t2 == wanted);
            var partial = !exact && wanted.Length > 3 &&
                          (t1.Contains(wanted) || wanted.Contains(t1) && t1.Length > 3 ||
                           t2.Contains(wanted) || wanted.Contains(t2) && t2.Length > 3);

            var delta = year.HasValue && r.Year.HasValue ? Math.Abs(year.Value - r.Year.Value) : 99;
            var score = (exact ? 100 : partial ? 40 : 0)
                        + (delta == 0 ? 50 : delta == 1 ? 25 : 0)
                        + (r.VoteCount > 50 ? 5 : 0);
            list.Add(new Candidate(r, score, exact, delta));
        }
        return list.OrderByDescending(c => c.Score).ThenByDescending(c => c.Item.Popularity).ToList();
    }

    public async Task<MatchOutcome> FindMatchAsync(string title, int? year, CancellationToken ct)
    {
        if (!_tmdb.IsConfigured) return new MatchOutcome(MatchStatus.Unmatched, null, "TMDB is not configured.");
        if (string.IsNullOrWhiteSpace(title)) return new MatchOutcome(MatchStatus.Unmatched, null, "No title to search for.");

        var results = await _tmdb.SearchAsync(title, year, ct);
        if (results.Count == 0 && year.HasValue) results = await _tmdb.SearchAsync(title, null, ct);
        if (results.Count == 0) return new MatchOutcome(MatchStatus.Unmatched, null, "No TMDB results for this title.");

        var ranked = Rank(results, title, year);
        var best = ranked[0];

        var confident = best.ExactTitle && (best.YearDelta == 0 || (!year.HasValue) || best.YearDelta == 1);
        if (confident)
        {
            // Another exact candidate with the same year makes the choice ambiguous (e.g. remakes in the same year).
            var rivals = ranked.Skip(1).Count(c => c.ExactTitle && c.YearDelta == best.YearDelta);
            if (rivals > 0 || (!year.HasValue && ranked.Count(c => c.ExactTitle) > 1)) confident = false;
        }

        if (confident)
            return new MatchOutcome(MatchStatus.Matched, best.Item.Id,
                $"Matched '{best.Item.Title}' ({best.Item.Year?.ToString() ?? "?"}).");

        var top = string.Join("; ", ranked.Take(3).Select(c => $"{c.Item.Title} ({c.Item.Year?.ToString() ?? "?"}) #{c.Item.Id}"));
        return new MatchOutcome(MatchStatus.Ambiguous, null, $"No confident match. Closest: {top}");
    }

    /// <summary>Searches TMDB for the admin "correct the match" screen.</summary>
    public async Task<List<TmdbSearchItem>> SearchForAdminAsync(string title, int? year, CancellationToken ct) =>
        await _tmdb.SearchAsync(title, year, ct);

    // ------------------------------------------------------------------ applying

    /// <summary>Searches TMDB for the movie, stores the id when the match is confident and copies the metadata. Never throws for TMDB problems.</summary>
    public async Task<MatchOutcome> MatchAndApplyAsync(AppDbContext db, Movie movie, string title, int? year, CancellationToken ct)
    {
        MatchOutcome outcome;
        try
        {
            outcome = await FindMatchAsync(title, year, ct);
            if (outcome.Status == MatchStatus.Matched && outcome.TmdbId is { } id)
                await ApplyTmdbIdAsync(db, movie, id, MatchStatus.Matched, ct);
        }
        catch (TmdbException ex)
        {
            _log.LogWarning("TMDB lookup failed for '{Title}': {Message}", title, ex.Message);
            outcome = new MatchOutcome(MatchStatus.Unmatched, null, "TMDB lookup failed: " + ex.Message);
        }

        if (outcome.Status != MatchStatus.Matched)
        {
            movie.MatchStatus = outcome.Status;
            movie.MatchNote = outcome.Note;
        }
        return outcome;
    }

    /// <summary>Fetches full TMDB details for an id and copies them onto the movie (used by scans and the admin correction screen).</summary>
    public async Task ApplyTmdbIdAsync(AppDbContext db, Movie movie, int tmdbId, MatchStatus status, CancellationToken ct)
    {
        var d = await _tmdb.GetDetailsAsync(tmdbId, ct)
                ?? throw new TmdbException($"TMDB has no movie with id {tmdbId}.");

        movie.TmdbId = d.Id;
        movie.ImdbId = d.ExternalIds?.ImdbId ?? d.ImdbId;
        if (!string.IsNullOrWhiteSpace(d.Title)) movie.Title = d.Title!;
        movie.OriginalTitle = string.IsNullOrWhiteSpace(d.OriginalTitle) ? null : d.OriginalTitle;
        movie.Overview = string.IsNullOrWhiteSpace(d.Overview) ? null : d.Overview;
        movie.Tagline = string.IsNullOrWhiteSpace(d.Tagline) ? null : d.Tagline;
        movie.ReleaseDate = TmdbDates.Parse(d.ReleaseDate);
        movie.ReleaseYear = d.Year ?? movie.ReleaseYear;
        movie.Runtime = d.Runtime is > 0 ? d.Runtime : movie.Runtime;
        movie.TmdbRating = d.VoteCount > 0 ? Math.Round(d.VoteAverage, 1) : null;
        movie.TmdbVoteCount = d.VoteCount;
        movie.Popularity = d.Popularity;
        movie.BackdropUrl = _tmdb.ImageUrl("w1280", d.BackdropPath) ?? movie.BackdropUrl;

        // LocalPosterUrl holds a poster image found inside the ISP movie folder; PosterUrl is what the UI shows.
        var tmdbPoster = _tmdb.ImageUrl("w500", d.PosterPath);
        movie.PosterUrl = (_crawler.PreferFolderPoster && !string.IsNullOrEmpty(movie.LocalPosterUrl))
            ? movie.LocalPosterUrl
            : tmdbPoster ?? movie.LocalPosterUrl ?? movie.PosterUrl;

        movie.Director = d.Credits?.Crew.Where(c => c.Job == "Director" && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => c.Name!).Distinct().Take(3).Aggregate((string?)null, (a, n) => a == null ? n : a + ", " + n);

        var cast = d.Credits?.Cast.OrderBy(c => c.Order).Where(c => !string.IsNullOrWhiteSpace(c.Name)).Take(14)
            .Select(c => new CastMember { Name = c.Name!, Character = c.Character, Photo = _tmdb.ImageUrl("w185", c.ProfilePath) })
            .ToList();
        movie.CastJson = cast is { Count: > 0 } ? JsonSerializer.Serialize(cast) : null;

        var companies = string.Join(", ", d.ProductionCompanies.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name!).Take(4));
        var countries = string.Join(", ", d.ProductionCountries.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name!).Take(3));
        movie.ProductionInfo = string.Join(" · ", new[] { companies, countries }.Where(s => s.Length > 0)) is { Length: > 0 } p ? p : null;

        if (_omdb.IsConfigured && !string.IsNullOrEmpty(movie.ImdbId))
            movie.ImdbRating = await _omdb.GetImdbRatingAsync(movie.ImdbId, ct) ?? movie.ImdbRating;

        // genres
        var genreIds = await _genres.EnsureAsync(d.Genres, ct);
        if (movie.Id != 0)
            await db.Entry(movie).Collection(m => m.MovieGenres).LoadAsync(ct);
        movie.MovieGenres.Clear();
        foreach (var gid in genreIds) movie.MovieGenres.Add(new MovieGenre { GenreId = gid });

        movie.MatchStatus = status;
        movie.MatchNote = $"TMDB #{d.Id}";
        movie.MetadataFetchedAt = DateTime.UtcNow;
        movie.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Removes the TMDB association but keeps the discovered entry and its sources.</summary>
    public static void ClearMatch(Movie movie, ParsedName parsed)
    {
        movie.TmdbId = null;
        movie.ImdbId = null;
        movie.MatchStatus = MatchStatus.Unmatched;
        movie.MatchNote = "Match removed by an administrator.";
        movie.Title = parsed.Title;
        movie.ReleaseYear = parsed.Year ?? movie.ReleaseYear;
        movie.UpdatedAt = DateTime.UtcNow;
    }
}
