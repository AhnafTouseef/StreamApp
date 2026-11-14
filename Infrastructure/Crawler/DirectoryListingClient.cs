using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using StreamApp.Configuration;

namespace StreamApp.Infrastructure.Crawler;

public sealed record DirEntry(string Name, string Url, bool IsDirectory, long? Size, DateTime? Modified);

public sealed record HttpProbeResult(
    string Url, int StatusCode, string? ContentType, long? ContentLength,
    string? AcceptRanges, string? ContentRange, string? CorsAllowOrigin, string? Error)
{
    public bool SupportsRanges => StatusCode == 206 ||
        string.Equals(AcceptRanges, "bytes", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Classifies file names using the configurable extension lists.</summary>
public sealed class MediaKinds
{
    private readonly HashSet<string> _video, _subtitle, _image;

    public MediaKinds(IOptions<CrawlerOptions> opt)
    {
        _video = new HashSet<string>(opt.Value.VideoExtensions, StringComparer.OrdinalIgnoreCase);
        _subtitle = new HashSet<string>(opt.Value.SubtitleExtensions, StringComparer.OrdinalIgnoreCase);
        _image = new HashSet<string>(opt.Value.ImageExtensions, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsVideo(string name) => _video.Contains(Path.GetExtension(name));
    public bool IsSubtitle(string name) => _subtitle.Contains(Path.GetExtension(name));
    public bool IsImage(string name) => _image.Contains(Path.GetExtension(name));
}

/// <summary>
/// Reads nginx/h5ai directory listings over HTTP. h5ai renders its index with JavaScript but also emits a plain
/// HTML fallback table (td.fb-n / fb-d / fb-s), which is what is parsed here. A plain nginx autoindex page is
/// handled as well. Every request goes through <see cref="SourcePolicy"/>; redirects are followed manually.
/// </summary>
public sealed class DirectoryListingClient
{
    public const string HttpClientName = "isp";

    private readonly IHttpClientFactory _http;
    private readonly SourcePolicy _policy;
    private readonly CrawlerOptions _opt;
    private readonly ILogger<DirectoryListingClient> _log;
    private readonly SemaphoreSlim _gate;

    public DirectoryListingClient(IHttpClientFactory http, SourcePolicy policy, IOptions<CrawlerOptions> opt, ILogger<DirectoryListingClient> log)
    {
        _http = http;
        _policy = policy;
        _opt = opt.Value;
        _log = log;
        _gate = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrentRequests));
    }

    public async Task<IReadOnlyList<DirEntry>> ListAsync(string dirUrl, CancellationToken ct)
    {
        var uri = NormalizeDirUri(dirUrl);
        var html = await GetStringWithRetryAsync(uri, ct);
        return ParseListing(html, uri);
    }

    /// <summary>
    /// Requests the first byte of a file to learn how the server behaves for playback: range support,
    /// content type and whether it sends CORS headers. Used by the admin panel before deciding on a proxy.
    /// </summary>
    public async Task<HttpProbeResult> ProbeAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !_policy.IsAllowed(uri))
            return new HttpProbeResult(url, 0, null, null, null, null, null, "URL is not under an approved source.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            req.Headers.TryAddWithoutValidation("Origin", "http://localhost");
            using var resp = await _http.CreateClient(HttpClientName)
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            string? Header(string name) => resp.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;
            long? length = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength;
            return new HttpProbeResult(url, (int)resp.StatusCode, resp.Content.Headers.ContentType?.ToString(), length,
                Header("Accept-Ranges"), resp.Content.Headers.ContentRange?.ToString(), Header("Access-Control-Allow-Origin"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new HttpProbeResult(url, 0, null, null, null, null, null, ex.Message);
        }
    }

    // ---------------------------------------------------------------- fetching

    private async Task<string> GetStringWithRetryAsync(Uri uri, CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await GetStringAsync(uri, ct);
            }
            catch (Exception ex) when (attempt < attempts && !ct.IsCancellationRequested &&
                                       (ex is HttpRequestException or TaskCanceledException or IOException))
            {
                var delay = TimeSpan.FromSeconds(attempt * 2);
                _log.LogWarning("Listing {Url} failed (attempt {Attempt}/{Max}): {Message}. Retrying in {Delay}s.",
                    uri, attempt, attempts, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_opt.DelayBetweenRequestsMs > 0) await Task.Delay(_opt.DelayBetweenRequestsMs, ct);
            var client = _http.CreateClient(HttpClientName);
            var current = uri;

            for (var hop = 0; hop <= _opt.MaxRedirects; hop++)
            {
                if (!_policy.IsAllowed(current))
                    throw new InvalidOperationException($"Blocked request to {current}: not under an approved source.");

                using var resp = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } location)
                {
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }
            throw new InvalidOperationException($"Too many redirects while listing {uri}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public static Uri NormalizeDirUri(string url)
    {
        var u = new Uri(url, UriKind.Absolute);
        return u.AbsolutePath.EndsWith('/') ? u : new UriBuilder(u) { Path = u.AbsolutePath + "/" }.Uri;
    }

    // ---------------------------------------------------------------- parsing

    private static readonly Regex RowRx = new(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnchorRx = new(@"<a\b[^>]*?href\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex CellDateRx = new(@"class\s*=\s*""[^""]*\bfb-d\b[^""]*""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CellSizeRx = new(@"class\s*=\s*""[^""]*\bfb-s\b[^""]*""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}(?::\d{2})?|\d{1,2}-[A-Za-z]{3}-\d{4}\s+\d{2}:\d{2})", RegexOptions.Compiled);
    private static readonly Regex SizeTailRx = new(@"(?:^|\s)(-|\d+(?:\.\d+)?\s?(?:[KMGT]i?B?|B)?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm",
        "dd-MMM-yyyy HH:mm", "d-MMM-yyyy HH:mm"
    };

    /// <summary>Parses an h5ai fallback table or an nginx autoindex page. Only direct children of <paramref name="dirUri"/> are returned.</summary>
    public static IReadOnlyList<DirEntry> ParseListing(string html, Uri dirUri)
    {
        var fragments = new List<(string Fragment, string? Date, string? Size)>();

        foreach (Match row in RowRx.Matches(html))
        {
            var inner = row.Groups[1].Value;
            if (!inner.Contains("<a", StringComparison.OrdinalIgnoreCase)) continue;
            fragments.Add((inner, CellText(CellDateRx, inner), CellText(CellSizeRx, inner)));
        }

        if (fragments.Count == 0)
        {
            foreach (var line in html.Split('\n'))
                if (line.Contains("<a", StringComparison.OrdinalIgnoreCase)) fragments.Add((line, null, null));
        }

        var dirPath = Uri.UnescapeDataString(dirUri.AbsolutePath);
        if (!dirPath.EndsWith('/')) dirPath += "/";

        var result = new Dictionary<string, DirEntry>(StringComparer.Ordinal);
        foreach (var (fragment, cellDate, cellSize) in fragments)
        {
            var anchor = AnchorRx.Match(fragment);
            if (!anchor.Success) continue;

            var href = WebUtility.HtmlDecode(anchor.Groups[1].Success ? anchor.Groups[1].Value : anchor.Groups[2].Value).Trim();
            if (href.Length == 0 || href[0] == '#' || href[0] == '?' ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;

            if (!Uri.TryCreate(dirUri, href, out var abs)) continue;
            if (!string.Equals(abs.Scheme, dirUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(abs.Host, dirUri.Host, StringComparison.OrdinalIgnoreCase) ||
                abs.Port != dirUri.Port) continue;

            var path = Uri.UnescapeDataString(abs.AbsolutePath);
            if (!path.StartsWith(dirPath, StringComparison.Ordinal) || path.Length == dirPath.Length) continue; // parent, self or outside

            var rest = path[dirPath.Length..];
            var isDir = rest.EndsWith('/');
            rest = rest.TrimEnd('/');
            if (rest.Length == 0 || rest.Contains('/')) continue; // not a direct child
            if (rest.StartsWith('.') || rest.StartsWith("_h5ai", StringComparison.OrdinalIgnoreCase)) continue;

            var url = abs.GetLeftPart(UriPartial.Path);
            if (isDir && !url.EndsWith('/')) url += "/";

            var tail = fragment[(anchor.Index + anchor.Length)..];
            var tailText = WebUtility.HtmlDecode(TagRx.Replace(tail, " ")).Trim();

            var dateText = cellDate;
            if (dateText == null)
            {
                var dm = DateRx.Match(tailText);
                if (dm.Success) dateText = dm.Value;
            }
            string? sizeText = cellSize;
            if (sizeText == null && tailText.Length > 0)
            {
                var withoutDate = DateRx.Replace(tailText, " ").Trim();
                var sm = SizeTailRx.Match(withoutDate);
                if (sm.Success) sizeText = sm.Groups[1].Value;
            }

            result[url] = new DirEntry(rest, url, isDir, isDir ? null : ParseSize(sizeText), ParseDate(dateText));
        }
        return result.Values.ToList();
    }

    private static string? CellText(Regex rx, string row)
    {
        var m = rx.Match(row);
        if (!m.Success) return null;
        var text = WebUtility.HtmlDecode(TagRx.Replace(m.Groups[1].Value, " ")).Trim();
        return text.Length == 0 ? null : text;
    }

    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text == "-") return null;
        var m = Regex.Match(text, @"^(\d+(?:\.\d+)?)\s*([KMGT]?)i?B?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        var factor = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => 1024d,
            "M" => 1024d * 1024,
            "G" => 1024d * 1024 * 1024,
            "T" => 1024d * 1024 * 1024 * 1024,
            _ => 1d
        };
        return (long)(value * factor);
    }
}
