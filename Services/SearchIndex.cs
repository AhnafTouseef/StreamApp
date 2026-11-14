using System.Globalization;
using Microsoft.EntityFrameworkCore;
using StreamApp.Data;
using StreamApp.Infrastructure.Crawler;

namespace StreamApp.Services;

/// <summary>One fuzzy search result: the movie and how well it matched (0..1).</summary>
public sealed record SearchHit(int Id, double Score, string Title);

/// <summary>
/// In-memory fuzzy title search ("intersteller", "spiderman", "lord rings"). It indexes every movie's title and original title, and follows the catalog by itself:
/// a scan, an admin edit or a delete changes the movie table, and the next search notices that (a cheap fingerprint query, at most every couple of seconds) and
/// rebuilds the index. It is also built once at startup (<see cref="SearchIndexWarmup"/>), so nothing has to be triggered by hand.
/// </summary>
public sealed class SearchIndex
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<SearchIndex> _log;
    private readonly SemaphoreSlim _build = new(1, 1);
    private volatile Snapshot? _snap;
    private long _checkedTicks;
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(2);

    public SearchIndex(IDbContextFactory<AppDbContext> factory, ILogger<SearchIndex> log) { _factory = factory; _log = log; }

    /// <summary>Number of movies in the current index (0 before the first build).</summary>
    public int Count => _snap?.Entries.Length ?? 0;

    /// <summary>Marks the index stale; the next search rebuilds it.</summary>
    public void Invalidate() { _snap = null; Interlocked.Exchange(ref _checkedTicks, 0); }

    /// <summary>Builds the index now if it is missing or the catalog changed since it was built.</summary>
    public async Task EnsureFreshAsync(CancellationToken ct = default)
    {
        var snap = _snap;
        var now = Environment.TickCount64;
        if (snap != null && now - Interlocked.Read(ref _checkedTicks) < RecheckAfter.TotalMilliseconds) return;

        await _build.WaitAsync(ct);
        try
        {
            snap = _snap;
            now = Environment.TickCount64;
            if (snap != null && now - Interlocked.Read(ref _checkedTicks) < RecheckAfter.TotalMilliseconds) return;

            await using var db = await _factory.CreateDbContextAsync(ct);
            var print = await FingerprintAsync(db, ct);
            if (snap == null || snap.Print != print)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var rows = await db.Movies.AsNoTracking()
                    .Select(m => new { m.Id, m.Title, m.OriginalTitle, m.Popularity }).ToListAsync(ct);
                _snap = Build(rows.Select(r => (r.Id, r.Title, r.OriginalTitle, r.Popularity)).ToList(), print);
                _log.LogInformation("Search index built for {Count} movies in {Ms} ms.", rows.Count, sw.ElapsedMilliseconds);
            }
            Interlocked.Exchange(ref _checkedTicks, Environment.TickCount64);
        }
        finally { _build.Release(); }
    }

    private static async Task<string> FingerprintAsync(AppDbContext db, CancellationToken ct)
    {
        var count = await db.Movies.CountAsync(ct);
        var maxId = await db.Movies.MaxAsync(m => (int?)m.Id, ct);
        var lastUpdate = await db.Movies.OrderByDescending(m => m.UpdatedAt).Select(m => (DateTime?)m.UpdatedAt).FirstOrDefaultAsync(ct);
        return $"{count}|{maxId}|{lastUpdate?.Ticks}";
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int max = 100, CancellationToken ct = default)
    {
        await EnsureFreshAsync(ct);
        return Search(query, max);
    }

    /// <summary>Type-ahead matches for a partly typed title (see <see cref="Snapshot.Suggest"/>).</summary>
    public async Task<IReadOnlyList<SearchHit>> SuggestAsync(string typed, int max = 8, CancellationToken ct = default)
    {
        await EnsureFreshAsync(ct);
        var snap = _snap;
        return snap == null ? Array.Empty<SearchHit>() : snap.Suggest(typed, max);
    }

    /// <summary>Searches the current index (empty before the first build).</summary>
    public IReadOnlyList<SearchHit> Search(string query, int max = 100)
    {
        var snap = _snap;
        if (snap == null || snap.Entries.Length == 0) return Array.Empty<SearchHit>();
        return snap.Search(query, max);
    }

    // ------------------------------------------------------------------ building

    /// <summary>Builds an index from raw rows. Public so tests can use it without a database.</summary>
    public static SearchIndex.Snapshot Build(IReadOnlyList<(int Id, string Title, string? OriginalTitle, double? Popularity)> rows, string print = "")
    {
        var entries = new Entry[rows.Count];
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var (id, title, original, pop) = rows[i];
            var variants = new List<Variant> { MakeVariant(title) };
            if (!string.IsNullOrWhiteSpace(original) && Norm(original) != Norm(title)) variants.Add(MakeVariant(original!));
            entries[i] = new Entry(id, title, pop ?? 0, variants.Where(v => v.Compact.Length > 0).ToArray());
            foreach (var tok in entries[i].Variants.SelectMany(v => v.Content).Distinct()) df[tok] = df.GetValueOrDefault(tok) + 1;
        }
        var n = Math.Max(1, rows.Count);
        var idf = new Dictionary<string, double>(df.Count, StringComparer.Ordinal);
        foreach (var (tok, d) in df) idf[tok] = Math.Log(1 + n / (double)d);
        return new Snapshot(entries, idf, Math.Log(1 + n), print);
    }

    internal sealed record Entry(int Id, string Title, double Popularity, Variant[] Variants);

    internal sealed class Variant
    {
        public string Compact = "";
        public string[] Tokens = Array.Empty<string>();   // every word
        public string[] Content = Array.Empty<string>();  // words without the, a, an, of, and
        public int[] Trigrams = Array.Empty<int>();
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal) { "the", "a", "an", "of", "and" };

    internal static string Norm(string? s) => FolderNameParser.Normalize(s);

    internal static Variant MakeVariant(string text)
    {
        var norm = Norm(text);
        var tokens = norm.Length == 0 ? Array.Empty<string>() : norm.Split(' ');
        var content = tokens.Where(t => !Stop.Contains(t)).ToArray();
        if (content.Length == 0) content = tokens;
        var compact = norm.Replace(" ", "");
        return new Variant { Compact = compact, Tokens = tokens, Content = content, Trigrams = Trigrams(compact) };
    }

    private static int[] Trigrams(string compact)
    {
        if (compact.Length < 3) return Array.Empty<int>();
        var set = new SortedSet<int>();
        var padded = "^" + compact + "$";
        for (var i = 0; i + 3 <= padded.Length; i++) set.Add(unchecked((padded[i] * 65599 + padded[i + 1]) * 65599 + padded[i + 2]));
        return set.ToArray();
    }

    private static double Dice(int[] a, int[] b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        int i = 0, j = 0, shared = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { shared++; i++; j++; }
            else if (a[i] < b[j]) i++;
            else j++;
        }
        return 2.0 * shared / (a.Length + b.Length);
    }

    /// <summary>Edit distance counting a swap of two neighbouring letters as one edit. Returns max+1 when it is more than <paramref name="max"/>.</summary>
    internal static int Distance(string a, string b, int max)
    {
        if (a == b) return 0;
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, prev2[j - 2] + 1);
                cur[j] = v;
                if (v < rowMin) rowMin = v;
            }
            if (rowMin > max) return max + 1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }

    // ------------------------------------------------------------------ searching

    public sealed class Snapshot
    {
        internal readonly Entry[] Entries;
        private readonly Dictionary<string, double> _idf;
        private readonly double _idfMax;
        internal readonly string Print;

        internal Snapshot(Entry[] entries, Dictionary<string, double> idf, double idfMax, string print)
        { Entries = entries; _idf = idf; _idfMax = idfMax; Print = print; }

        public int Count => Entries.Length;

        /// <summary>Anything scoring below this is not a match.</summary>
        public const double MinScore = 0.62;

        public IReadOnlyList<SearchHit> Search(string query, int max = 100)
        {
            var q = MakeVariant(query);
            if (q.Compact.Length == 0) return Array.Empty<SearchHit>();

            var hits = new List<(Entry E, double S)>();
            foreach (var e in Entries)
            {
                var best = 0.0;
                foreach (var v in e.Variants) best = Math.Max(best, Score(q, v));
                if (best >= MinScore) hits.Add((e, best));
            }
            return hits.OrderByDescending(h => Math.Round(h.S, 2)).ThenByDescending(h => h.E.Popularity).ThenBy(h => h.E.Id)
                .Take(Math.Max(1, max)).Select(h => new SearchHit(h.E.Id, h.S, h.E.Title)).ToList();
        }

        /// <summary>
        /// Matches for letters typed so far, best first: titles that start with the text, then titles where every typed word starts a word of the title
        /// (any order), then titles that contain the text, then similar titles (typos) once at least three letters are typed.
        /// </summary>
        public IReadOnlyList<SearchHit> Suggest(string typed, int max = 8)
        {
            var q = MakeVariant(typed);
            if (q.Compact.Length == 0) return Array.Empty<SearchHit>();
            var hits = new List<(Entry E, double S)>();
            foreach (var e in Entries)
            {
                var best = 0.0;
                foreach (var v in e.Variants) best = Math.Max(best, SuggestScore(q, v));
                if (best > 0) hits.Add((e, best));
            }
            return hits.OrderByDescending(h => Math.Round(h.S, 1)).ThenByDescending(h => h.E.Popularity).ThenBy(h => h.E.Title, StringComparer.Ordinal)
                .Take(Math.Max(1, max)).Select(h => new SearchHit(h.E.Id, h.S, h.E.Title)).ToList();
        }

        private double SuggestScore(Variant q, Variant v)
        {
            if (v.Compact.StartsWith(q.Compact, StringComparison.Ordinal)) return 1.0;
            if (q.Tokens.Length > 1 || q.Compact.Length >= 2)
            {
                // every typed word starts a different word of the title: "rings lord", "dark kni"
                var used = new bool[v.Tokens.Length]; var all = true;
                foreach (var qt in q.Tokens)
                {
                    var found = -1;
                    for (var i = 0; i < v.Tokens.Length; i++)
                        if (!used[i] && v.Tokens[i].StartsWith(qt, StringComparison.Ordinal)) { found = i; break; }
                    if (found < 0) { all = false; break; }
                    used[found] = true;
                }
                if (all) return 0.95;
            }
            if (q.Compact.Length >= 2 && v.Compact.Contains(q.Compact, StringComparison.Ordinal)) return 0.8;
            if (q.Compact.Length < 3) return 0;
            var fuzzy = Score(q, v);
            return fuzzy >= MinScore ? fuzzy : 0;
        }

        private double Score(Variant q, Variant v)
        {
            // 1) same letters with other spacing or punctuation: "spiderman" / "spider man", "starwars" / "star wars"
            var compact = 0.0;
            if (q.Compact.Length >= 4 && v.Compact.Contains(q.Compact, StringComparison.Ordinal))
                compact = v.Compact.Length == q.Compact.Length ? 1.0 : 0.9;

            // 2) word by word, in any order, forgiving small typos
            var token = TokenScore(q, v, out var anchored);

            // 3) overall letter-group overlap: catches words that were merged or split, and bigger slips
            var dice = Dice(q.Trigrams, v.Trigrams) * 0.8;

            var score = Math.Max(compact, Math.Max(token, dice));
            // a score that rests on typo-matching alone needs one solid word (or strong letter overlap)
            if (compact == 0 && !anchored && dice < MinScore) return 0;
            return score;
        }

        private double TokenScore(Variant q, Variant v, out bool anchored)
        {
            anchored = false;
            if (q.Content.Length == 0 || v.Content.Length == 0) return 0;
            var used = new bool[v.Content.Length];
            double sum = 0, weights = 0;
            foreach (var qt in q.Content)
            {
                var w = _idf.TryGetValue(qt, out var known) ? known : _idfMax;
                var bestSim = 0.0; var bestIdx = -1;
                for (var i = 0; i < v.Content.Length; i++)
                {
                    var sim = TokenSim(qt, v.Content[i]);
                    if (sim > bestSim) { bestSim = sim; bestIdx = i; }
                }
                if (bestIdx >= 0 && bestSim >= 0.75) { used[bestIdx] = true; if (bestSim >= 0.85) anchored = true; }
                else bestSim = 0;
                sum += w * bestSim; weights += w;
            }
            if (weights <= 0) return 0;
            var score = sum / weights;
            var unmatched = used.Count(u => !u);
            return score * (1 - 0.03 * Math.Min(unmatched, 6));
        }

        private static double TokenSim(string q, string t)
        {
            if (q == t) return 1.0;
            if (q.Length >= 3 && t.Length > q.Length && t.StartsWith(q, StringComparison.Ordinal)) return 0.9;   // typed so far
            var allowed = q.Length <= 3 ? 0 : q.Length <= 7 ? 1 : 2;
            if (allowed == 0) return 0;
            var d = Distance(q, t, allowed);
            return d > allowed ? 0 : 1.0 - 0.1 * d;
        }
    }
}

/// <summary>Builds the search index in the background once the app is up, so the first search is instant and nothing has to be started by hand.</summary>
public sealed class SearchIndexWarmup : BackgroundService
{
    private readonly SearchIndex _index;
    private readonly ILogger<SearchIndexWarmup> _log;
    public SearchIndexWarmup(SearchIndex index, ILogger<SearchIndexWarmup> log) { _index = index; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Yield(); await _index.EnsureFreshAsync(stoppingToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Could not build the search index at startup; it will be built on the first search."); }
    }
}
