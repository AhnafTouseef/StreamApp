using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StreamApp.Data;
using StreamApp.Infrastructure.Crawler;
using StreamApp.Models;

namespace StreamApp.Infrastructure.Metadata;

/// <summary>TV side of the metadata service: series are matched against TMDB TV (never movie) data, and season/episode details are copied onto the local rows.</summary>
public sealed partial class MetadataService
{
    // ------------------------------------------------------------------ matching a series directory name

    public sealed record SeriesCandidate(TmdbSearchItem Item, int Score, int TitleScore, int YearDelta, bool? RegionMatch);

    /// <summary>
    /// Matches a parsed series directory name against TMDB TV. The title decides; the year is only a clue (a provider's 2026 may be TMDB's 2025,
    /// so TMDB is searched without a year filter and a difference of one year is accepted); a "(US)"-style qualifier separates same-named shows.
    /// The best candidate is then verified against its TMDB details (title, whether the year falls inside the show's run, whether TMDB has at least
    /// the seasons found locally) before it is associated. Anything less certain is returned as Ambiguous (needs review) with the closest candidates.
    /// </summary>
    public async Task<MatchOutcome> FindSeriesMatchAsync(SeriesName name, IReadOnlyCollection<int>? localSeasons, CancellationToken ct) =>
        (await FindSeriesMatchCoreAsync(name, localSeasons, ct)).Outcome;

    private sealed record SeriesMatchResult(MatchOutcome Outcome, TmdbTvDetails? Details);

    private static int TitleScore(TmdbSearchItem r, SeriesName n)
    {
        var best = 0;
        var names = new[] { FolderNameParser.Normalize(r.Title), FolderNameParser.Normalize(r.OriginalTitle) }.Where(x => x.Length > 0).ToList();
        for (var qi = 0; qi < n.Queries.Count; qi++)
        {
            var q = FolderNameParser.Normalize(n.Queries[qi]);
            if (q.Length == 0) continue;
            var cap = qi == 0 ? 100 : qi == 1 ? 98 : 94;   // the title as written beats a trimmed or split variant
            foreach (var t in names)
            {
                int sc;
                if (t == q) sc = cap;
                else if (StripThe(t) == StripThe(q)) sc = Math.Min(cap, 95);
                else
                {
                    var tw = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    var qw = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                    var inter = tw.Intersect(qw).Count();
                    var jac = (double)inter / Math.Max(1, tw.Union(qw).Count());
                    sc = jac >= 0.75 ? 60 : (t.Length > 3 && q.Length > 3 && (t.Contains(q) || q.Contains(t))) ? 40 : 0;
                }
                best = Math.Max(best, sc);
            }
        }
        return best;
    }

    private static string StripThe(string s) => s.StartsWith("the ", StringComparison.Ordinal) ? s[4..] : s;

    private static string CountryCode(string region) => region.Equals("UK", StringComparison.OrdinalIgnoreCase) ? "GB" : region.ToUpperInvariant();

    public static List<SeriesCandidate> RankSeries(IEnumerable<TmdbSearchItem> results, SeriesName n)
    {
        var list = new List<SeriesCandidate>();
        foreach (var r in results)
        {
            var ts = TitleScore(r, n);
            var delta = n.Year.HasValue && r.Year.HasValue ? Math.Abs(n.Year.Value - r.Year.Value) : 99;
            bool? region = n.Region != null && r.OriginCountry is { Count: > 0 } oc ? oc.Contains(CountryCode(n.Region), StringComparer.OrdinalIgnoreCase) : null;
            var score = ts + (delta == 0 ? 40 : delta == 1 ? 30 : delta == 2 ? 8 : 0) + (region == true ? 20 : region == false ? -20 : 0) + (r.VoteCount > 50 ? 5 : 0);
            list.Add(new SeriesCandidate(r, score, ts, delta, region));
        }
        return list.OrderByDescending(c => c.Score).ThenByDescending(c => c.Item.Popularity).ToList();
    }

    private static string Describe(IEnumerable<SeriesCandidate> ranked) =>
        string.Join("; ", ranked.Take(3).Select(c => $"{c.Item.Title} ({c.Item.Year?.ToString() ?? "?"}) #{c.Item.Id}"));

    private async Task<SeriesMatchResult> FindSeriesMatchCoreAsync(SeriesName name, IReadOnlyCollection<int>? localSeasons, CancellationToken ct)
    {
        MatchOutcome Done(MatchStatus st, int? id, string note) => new(st, id, note);
        if (!_tmdb.IsConfigured) return new(Done(MatchStatus.Unmatched, null, "TMDB is not configured."), null);
        if (string.IsNullOrWhiteSpace(name.Title)) return new(Done(MatchStatus.Unmatched, null, "No title to search for."), null);

        // An id written into the folder name by the provider is the strongest hint there is - but it is still checked to exist as a TV series.
        if (name.TmdbId is { } hinted && await _tmdb.GetTvDetailsAsync(hinted, ct) is { } hd)
            return new(Done(MatchStatus.Matched, hinted, $"Matched '{hd.Name}' ({hd.Year?.ToString() ?? "?"}) by the TMDB id in the folder name."), hd);

        var pool = new Dictionary<int, TmdbSearchItem>();
        foreach (var q in name.Queries.Take(4))
        {
            foreach (var r in await _tmdb.SearchTvAsync(q, null, ct)) pool.TryAdd(r.Id, r);
            var nq = FolderNameParser.Normalize(q);
            var exact = pool.Values.Any(r => FolderNameParser.Normalize(r.Title) == nq || FolderNameParser.Normalize(r.OriginalTitle) == nq);
            // TMDB's year filter is exact, so it is only used to dig deeper for a common title - never to exclude a neighbouring year.
            if (!exact && name.Year is { } y)
                foreach (var r in await _tmdb.SearchTvAsync(q, y, ct)) pool.TryAdd(r.Id, r);
            if (exact) break;
        }
        if (pool.Count == 0) return new(Done(MatchStatus.Unmatched, null, $"No TMDB TV results for '{name.Title}'."), null);

        var ranked = RankSeries(pool.Values, name);
        var best = ranked[0];
        if (best.TitleScore < 40)
            return new(Done(MatchStatus.Unmatched, null, $"No TMDB TV title resembles '{name.Title}'. Closest: {Describe(ranked)}"), null);
        if (best.TitleScore < 94)
            return new(Done(MatchStatus.Ambiguous, null, $"No exact title match for '{name.Title}'. Closest: {Describe(ranked)}"), null);

        // another show with the same title that the year / country does not rule out makes the choice a human one
        bool Compatible(SeriesCandidate c) => c.TitleScore >= 94 && c.RegionMatch != false && (!name.Year.HasValue || c.YearDelta <= 1 || c.YearDelta == 99);
        var rivals = ranked.Skip(1).Where(c => Compatible(c) && !(name.Year.HasValue && c.YearDelta > best.YearDelta && best.YearDelta <= 1) && !(best.RegionMatch == true && c.RegionMatch != true)).ToList();
        if (rivals.Count > 0)
        {
            var runnerUp = rivals.Max(c => c.Item.Popularity);
            var dominant = best.Item.Popularity >= 3 * Math.Max(runnerUp, 1) && best.Item.VoteCount >= 100;
            if (!dominant) return new(Done(MatchStatus.Ambiguous, null, $"Several shows are titled '{name.Title}'. Closest: {Describe(ranked)}"), null);
        }

        // ---- verify the candidate against its details before associating it
        var d = await _tmdb.GetTvDetailsAsync(best.Item.Id, ct);
        if (d == null) return new(Done(MatchStatus.Ambiguous, null, $"TMDB #{best.Item.Id} could not be read. Closest: {Describe(ranked)}"), null);

        var check = Verify(d, name, localSeasons);
        if (check != null) return new(Done(MatchStatus.Ambiguous, null, $"Possible: {d.Name} ({d.Year?.ToString() ?? "?"}) #{d.Id} - {check}. Closest: {Describe(ranked)}"), null);

        var yearNote = name.Year.HasValue && d.Year.HasValue && name.Year != d.Year ? $", folder year {name.Year} vs TMDB {d.Year}" : "";
        return new(Done(MatchStatus.Matched, d.Id, $"Matched '{d.Name}' ({d.Year?.ToString() ?? "?"}){yearNote}."), d);
    }

    /// <summary>Null when the candidate is consistent with what the directory says; otherwise the reason it needs a human look.</summary>
    private static string? Verify(TmdbTvDetails d, SeriesName n, IReadOnlyCollection<int>? localSeasons)
    {
        if (n.Year is { } y && d.Year is { } first)
        {
            var ended = string.Equals(d.Status, "Ended", StringComparison.OrdinalIgnoreCase) || string.Equals(d.Status, "Canceled", StringComparison.OrdinalIgnoreCase);
            var last = TmdbDates.YearOf(d.LastAirDate) ?? first;
            var upper = ended ? last : Math.Max(last, DateTime.UtcNow.Year);
            var lo = first - 1;                       // international / festival release timing
            var hi = upper + 1;
            if (y < lo || y > hi) return $"the folder year {y} is outside the show's run ({first}-{(ended ? last.ToString() : "")})";
        }
        if (localSeasons is { Count: > 0 } && d.NumberOfSeasons is { } count && count > 0)
        {
            var max = localSeasons.Max();
            if (max > count + 1) return $"the folder has season {max} but TMDB lists {count}";
        }
        return null;
    }

    /// <summary>Searches TMDB TV for the series entry, stores the id when the match is confident and copies series, season and episode metadata. Never throws for TMDB problems.</summary>
    public async Task<MatchOutcome> MatchAndApplySeriesAsync(AppDbContext db, Movie movie, SeriesName name, CancellationToken ct)
    {
        MatchOutcome outcome;
        try
        {
            IReadOnlyCollection<int>? local = movie.Series?.Seasons.Where(s => s.Number > 0 && !s.IsCustom).Select(s => s.Number).ToList();
            var r = await FindSeriesMatchCoreAsync(name, local, ct);
            outcome = r.Outcome;
            if (outcome.Status == MatchStatus.Matched && outcome.TmdbId is { } id)
            {
                if (r.Details != null) await ApplySeriesDetailsAsync(db, movie, r.Details, MatchStatus.Matched, ct);
                else await ApplySeriesTmdbIdAsync(db, movie, id, MatchStatus.Matched, ct);
            }
        }
        catch (TmdbException ex)
        {
            _log.LogWarning("TMDB lookup failed for series '{Title}': {Message}", name.Title, ex.Message);
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
    public async Task ApplySeriesTmdbIdAsync(AppDbContext db, Movie movie, int tmdbId, MatchStatus status, CancellationToken ct) =>
        await ApplySeriesDetailsAsync(db, movie, await _tmdb.GetTvDetailsAsync(tmdbId, ct)
                ?? throw new TmdbException($"TMDB has no TV series with id {tmdbId}."), status, ct);

    private async Task ApplySeriesDetailsAsync(AppDbContext db, Movie movie, TmdbTvDetails d, MatchStatus status, CancellationToken ct)
    {

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
            if (season.IsCustom) continue;   // a folder-named group ("OVA", "Saga 2") is not a TMDB season: keep its own name and numbering
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
