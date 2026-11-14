using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamApp.Configuration;

namespace StreamApp.Infrastructure.Metadata;

public sealed class TmdbException : Exception
{
    public HttpStatusCode? Status { get; }
    public TmdbException(string message, HttpStatusCode? status = null, Exception? inner = null) : base(message, inner) => Status = status;
}

/// <summary>Thin TMDB v3 client. Accepts either a v3 API key (query string) or a v4 read-access token (Bearer).</summary>
public sealed class TmdbClient
{
    public const string HttpClientName = "tmdb";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _http;
    private readonly TmdbOptions _opt;
    private readonly ILogger<TmdbClient> _log;
    private readonly SemaphoreSlim _gate;

    public TmdbClient(IHttpClientFactory http, IOptions<TmdbOptions> opt, ILogger<TmdbClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
        _gate = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrency));
    }

    public bool IsConfigured => _opt.IsConfigured;

    public string? ImageUrl(string size, string? path) =>
        string.IsNullOrEmpty(path) ? null : $"{_opt.ImageBaseUrl.TrimEnd('/')}/{size}{path}";

    public async Task<List<TmdbSearchItem>> SearchAsync(string title, int? year, CancellationToken ct)
    {
        var q = new Dictionary<string, string> { ["query"] = title, ["include_adult"] = "false", ["page"] = "1" };
        if (year is { } y) q["primary_release_year"] = y.ToString();
        var resp = await GetAsync<TmdbSearchResponse>("/search/movie", q, ct);
        return resp?.Results ?? new List<TmdbSearchItem>();
    }

    public Task<TmdbMovieDetails?> GetDetailsAsync(int tmdbId, CancellationToken ct) =>
        GetAsync<TmdbMovieDetails>($"/movie/{tmdbId}",
            new Dictionary<string, string> { ["append_to_response"] = "credits,external_ids" }, ct);

    private async Task<T?> GetAsync<T>(string path, IDictionary<string, string> query, CancellationToken ct) where T : class
    {
        if (!_opt.IsConfigured) throw new TmdbException("TMDB is not configured (set Tmdb:ApiKey).");

        var isBearer = _opt.ApiKey.Contains('.');
        var parts = query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}").ToList();
        parts.Add($"language={Uri.EscapeDataString(_opt.Language)}");
        if (!isBearer) parts.Add($"api_key={Uri.EscapeDataString(_opt.ApiKey)}");
        var url = $"{_opt.ApiBaseUrl.TrimEnd('/')}{path}?{string.Join("&", parts)}";

        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (isBearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);

                HttpResponseMessage resp;
                try
                {
                    resp = await _http.CreateClient(HttpClientName).SendAsync(req, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    if (attempt >= 3) throw new TmdbException($"TMDB unreachable: {ex.Message}", null, ex);
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                    continue;
                }

                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                        throw new TmdbException("TMDB rejected the API key (401). Check Tmdb:ApiKey.", resp.StatusCode);

                    if ((resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500) && attempt < 3)
                    {
                        var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 2);
                        _log.LogWarning("TMDB returned {Status}; retrying in {Seconds}s.", (int)resp.StatusCode, wait.TotalSeconds);
                        await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct);
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode)
                        throw new TmdbException($"TMDB returned HTTP {(int)resp.StatusCode}.", resp.StatusCode);

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
