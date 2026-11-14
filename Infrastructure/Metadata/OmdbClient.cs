using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StreamApp.Configuration;

namespace StreamApp.Infrastructure.Metadata;

/// <summary>
/// Optional IMDb rating lookup through the official OMDb API (https://www.omdbapi.com/), keyed by the IMDb id TMDB provides.
/// IMDb pages are never scraped. Without an OMDb key the IMDb rating simply stays empty.
/// </summary>
public sealed class OmdbClient
{
    public const string HttpClientName = "omdb";
    private readonly IHttpClientFactory _http;
    private readonly OmdbOptions _opt;
    private readonly ILogger<OmdbClient> _log;

    public OmdbClient(IHttpClientFactory http, IOptions<OmdbOptions> opt, ILogger<OmdbClient> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public bool IsConfigured => _opt.IsConfigured;

    public async Task<double?> GetImdbRatingAsync(string imdbId, CancellationToken ct)
    {
        if (!_opt.IsConfigured || string.IsNullOrWhiteSpace(imdbId)) return null;
        try
        {
            var url = $"https://www.omdbapi.com/?i={Uri.EscapeDataString(imdbId)}&apikey={Uri.EscapeDataString(_opt.ApiKey)}";
            using var resp = await _http.CreateClient(HttpClientName).GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("imdbRating", out var r) &&
                double.TryParse(r.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
                return rating;
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("OMDb lookup for {ImdbId} failed: {Message}", imdbId, ex.Message);
            return null;
        }
    }
}
