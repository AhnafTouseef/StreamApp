using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreamApp.Data;
using StreamApp.Infrastructure.Crawler;
using StreamApp.Models;

namespace StreamApp.Infrastructure.Metadata;

/// <summary>TV side of the metadata service: series are matched against TMDB TV (never movie) data, and season/episode details are copied onto the local rows.</summary>
public sealed partial class MetadataService
{
    public async Task<MatchOutcome> FindSeriesMatchAsync(string title, int? year, CancellationToken ct)
    {
        if (!_tmdb.IsConfigured) return new MatchOutcome(MatchStatus.Unmatched, null, "TMDB is not configured.");
        if (string.IsNullOrWhiteSpace(title)) return new MatchOutcome(MatchStatus.Unmatched, null, "No title to search for.");

        var results = await _tmdb.SearchTvAsync(title, year, ct);
        if (results.Count == 0 && year.HasValue) results = await _tmdb.SearchTvAsync(title, null, ct);
        if (results.Count == 0) return new MatchOutcome(MatchStatus.Unmatched, null, "No TMDB TV results for this title.");

        var ranked = Rank(results, title, year);
        var best = ranked[0];

        var confident = best.ExactTitle && (best.YearDelta == 0 || !year.HasValue || best.YearDelta == 1);
        if (confident)
        {
            var rivals = ranked.Skip(1).Where(c => c.ExactTitle && c.YearDelta == best.YearDelta).ToList();
            var tie = rivals.Count > 0 || (!year.HasValue && ranked.Count(c => c.ExactTitle) > 1);
            // Series folders rarely carry a year. With several exact-title shows, the clearly more popular one wins; otherwise an admin decides.
            if (tie)
            {
                var runnerUp = ranked.Skip(1).Where(c => c.ExactTitle).Select(c => c.Item.Popularity).DefaultIfEmpty(0).Max();
                if (!(best.Item.Popularity >= 3 * Math.Max(runnerUp, 1) && best.Item.VoteCount >= 100)) confident = false;
            }
        }

        if (confident)
            return new MatchOutcome(MatchStatus.Matched, best.Item.Id, $"Matched '{best.Item.Title}' ({best.Item.Year?.ToString() ?? "?"}).");

        var top = string.Join("; ", ranked.Take(3).Select(c => $"{c.Item.Title} ({c.Item.Year?.ToString() ?? "?"}) #{c.Item.Id}"));
        return new MatchOutcome(MatchStatus.Ambiguous, null, $"No confident match. Closest: {top}");
    }

    /// <summary>Searches TMDB TV for the series entry, stores the id when the match is confident and copies series, season and episode metadata. Never throws for TMDB problems.</summary>
    public async Task<MatchOutcome> MatchAndApplySeriesAsync(AppDbContext db, Movie movie, string title, int? year, CancellationToken ct)
    {
        MatchOutcome outcome;
        try
        {
            outcome = await FindSeriesMatchAsync(title, year, ct);
            if (outcome.Status == MatchStatus.Matched && outcome.TmdbId is { } id)
                await ApplySeriesTmdbIdAsync(db, movie, id, MatchStatus.Matched, ct);
        }
        catch (TmdbException ex)
        {
            _log.LogWarning("TMDB lookup failed for series '{Title}': {Message}", title, ex.Message);
            outcome = new MatchOutcome(MatchStatus.Unmatched, null, "TMDB lookup failed: " + ex.Message);
        }

        if (outcome.Status != MatchStatus.Matched)
        {
            movie.MatchStatus = outcome.Status;
            movie.MatchNote = outcome.Note;
        }
        return outcome;
    }

    /// <summary>Copies TMDB TV details onto the catalog entry and its <see cref="Series"/> row, then fills every local season and episode.</summary>
    public async Task ApplySeriesTmdbIdAsync(AppDbContext db, Movie movie, int tmdbId, MatchStatus status, CancellationToken ct)
    {
        var d = await _tmdb.GetTvDetailsAsync(tmdbId, ct)
                ?? throw new TmdbException($"TMDB has no TV series with id {tmdbId}.");

        movie.ContentType = CatalogType.Series;
        movie.TmdbId = d.Id;
        movie.ImdbId = d.ExternalIds?.ImdbId;
        if (!string.IsNullOrWhiteSpace(d.Name)) movie.Title = d.Name!;
        movie.OriginalTitle = string.IsNullOrWhiteSpace(d.OriginalName) ? null : d.OriginalName;
        movie.Overview = string.IsNullOrWhiteSpace(d.Overview) ? null : d.Overview;
        movie.Tagline = string.IsNullOrWhiteSpace(d.Tagline) ? null : d.Tagline;
        movie.ReleaseDate = TmdbDates.Parse(d.FirstAirDate);
        movie.ReleaseYear = d.Year ?? movie.ReleaseYear;
        movie.Runtime = d.EpisodeRunTime.FirstOrDefault(r => r > 0) is > 0 and var rt ? rt : movie.Runtime;
        movie.TmdbRating = d.VoteCount > 0 ? Math.Round(d.VoteAverage, 1) : null;
        movie.TmdbVoteCount = d.VoteCount;
        movie.Popularity = d.Popularity;
        movie.BackdropUrl = _tmdb.ImageUrl("w1280", d.BackdropPath) ?? movie.BackdropUrl;

        var tmdbPoster = _tmdb.ImageUrl("w500", d.PosterPath);
        // The folder's first image is only the starting thumbnail: a TMDB poster replaces it as soon as there is one.
        movie.PosterUrl = tmdbPoster ?? movie.LocalPosterUrl ?? movie.PosterUrl;

        movie.Director = string.Join(", ", d.CreatedBy.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name!).Distinct().Take(3)) is { Length: > 0 } cb ? cb : null;   // "Created by" on the series page

        var cast = d.Credits?.Cast.OrderBy(c => c.Order).Where(c => !string.IsNullOrWhiteSpace(c.Name)).Take(14)
            .Select(c => new CastMember { Name = c.Name!, Character = c.Character, Photo = _tmdb.ImageUrl("w185", c.ProfilePath) }).ToList();
        movie.CastJson = cast is { Count: > 0 } ? JsonSerializer.Serialize(cast) : null;

        var networks = string.Join(", ", d.Networks.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name!).Take(3));
        var countries = string.Join(", ", d.ProductionCountries.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Name!).Take(3));
        movie.ProductionInfo = string.Join(" · ", new[] { networks, countries }.Where(x => x.Length > 0)) is { Length: > 0 } p ? p : null;

        if (_omdb.IsConfigured && !string.IsNullOrEmpty(movie.ImdbId))
            movie.ImdbRating = await _omdb.GetImdbRatingAsync(movie.ImdbId, ct) ?? movie.ImdbRating;

        var genreIds = await _genres.EnsureAsync(d.Genres, ct);
        if (movie.Id != 0 && db.Entry(movie).State != EntityState.Added)
            await db.Entry(movie).Collection(m => m.MovieGenres).LoadAsync(ct);
        movie.MovieGenres.Clear();
        foreach (var gid in genreIds) movie.MovieGenres.Add(new MovieGenre { GenreId = gid });

        var series = await EnsureSeriesAsync(db, movie, ct);
        series.Status = string.IsNullOrWhiteSpace(d.Status) ? null : d.Status;
        series.Networks = networks.Length > 0 ? networks : null;
        series.LastAirDate = TmdbDates.Parse(d.LastAirDate);
        series.TmdbSeasonCount = d.NumberOfSeasons;
        series.TmdbEpisodeCount = d.NumberOfEpisodes;

        movie.MatchStatus = status;
        movie.MatchNote = $"TMDB TV #{d.Id}";
        movie.MetadataFetchedAt = DateTime.UtcNow;
        movie.UpdatedAt = DateTime.UtcNow;

        await ApplySeasonMetadataAsync(db, movie, null, ct);
    }

    /// <summary>
    /// Fills season and episode rows from TMDB (one request per season). <paramref name="onlySeasons"/> limits the work to seasons that just changed.
    /// Episodes TMDB does not know keep their file-derived number and get no title. Failures of a single season never fail the series.
    /// </summary>
    public async Task ApplySeasonMetadataAsync(AppDbContext db, Movie movie, ICollection<int>? onlySeasons, CancellationToken ct)
    {
        if (movie.TmdbId is not { } tmdbId) return;
        var series = await EnsureSeriesAsync(db, movie, ct);
        await EnsureSeasonsLoadedAsync(db, series, ct);

        foreach (var season in series.Seasons.OrderBy(s => s.Number))
        {
            if (onlySeasons != null && !onlySeasons.Contains(season.Number)) continue;
            TmdbSeasonDetails? td;
            try { td = await _tmdb.GetTvSeasonAsync(tmdbId, season.Number, ct); }
            catch (TmdbException ex)
            {
                _log.LogWarning("TMDB season {Season} of #{Id} failed: {Message}", season.Number, tmdbId, ex.Message);
                continue;
            }
            if (td == null) continue;

            season.TmdbSeasonId = td.Id;
            if (!string.IsNullOrWhiteSpace(td.Name)) season.Name = td.Name;
            if (!string.IsNullOrWhiteSpace(td.Overview)) season.Overview = td.Overview;
            season.AirDate = TmdbDates.Parse(td.AirDate) ?? season.AirDate;
            season.PosterUrl = _tmdb.ImageUrl("w342", td.PosterPath) ?? season.PosterUrl;

            var byNumber = td.Episodes.GroupBy(e => e.EpisodeNumber).ToDictionary(g => g.Key, g => g.First());
            foreach (var ep in season.Episodes)
            {
                if (!byNumber.TryGetValue(ep.Number, out var te)) continue;
                var titles = new List<string>();
                for (var n = ep.Number; n <= (ep.EndNumber ?? ep.Number) && titles.Count < 3; n++)
                    if (byNumber.TryGetValue(n, out var x) && !string.IsNullOrWhiteSpace(x.Name)) titles.Add(x.Name!);
                ep.Title = titles.Count > 0 ? string.Join(" / ", titles) : ep.Title;
                ep.Overview = string.IsNullOrWhiteSpace(te.Overview) ? ep.Overview : te.Overview;
                ep.AirDate = TmdbDates.Parse(te.AirDate) ?? ep.AirDate;
                ep.Runtime = te.Runtime is > 0 ? te.Runtime : ep.Runtime;
                ep.StillUrl = _tmdb.ImageUrl("w300", te.StillPath) ?? ep.StillUrl;
                ep.TmdbRating = te.VoteCount > 0 ? Math.Round(te.VoteAverage, 1) : ep.TmdbRating;
            }
        }
    }

    private static async Task<Series> EnsureSeriesAsync(AppDbContext db, Movie movie, CancellationToken ct)
    {
        if (movie.Series == null && movie.Id != 0 && db.Entry(movie).State != EntityState.Added)
            await db.Entry(movie).Reference(m => m.Series).LoadAsync(ct);
        return movie.Series ??= new Series();
    }

    private static async Task EnsureSeasonsLoadedAsync(AppDbContext db, Series series, CancellationToken ct)
    {
        var entry = db.Entry(series);
        if (entry.State is EntityState.Added or EntityState.Detached) return;   // a freshly built graph is already complete in memory
        if (!entry.Collection(s => s.Seasons).IsLoaded)
            await entry.Collection(s => s.Seasons).Query().Include(s => s.Episodes).LoadAsync(ct);
    }
}
