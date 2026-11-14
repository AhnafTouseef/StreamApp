using Microsoft.EntityFrameworkCore;
using StreamApp.Data;

namespace StreamApp.Infrastructure.Crawler;

/// <summary>
/// The single gate for outbound requests to content servers. A URL is allowed only when it lives under the
/// root URL of an administrator-registered source (same scheme, host, port and path prefix). The crawler,
/// ffprobe, the stream proxy and the subtitle endpoint all check it, and redirects are re-checked per hop.
/// </summary>
public sealed class SourcePolicy
{
    private volatile Uri[] _roots = Array.Empty<Uri>();

    public IReadOnlyList<Uri> Roots => _roots;

    /// <summary>
    /// Hosts that can never be a content source: link-local addresses (169.254.0.0/16, fe80::/10 - cloud metadata services live there)
    /// and the unspecified address. Private LAN ranges stay allowed because ISP media servers are typically on one.
    /// </summary>
    public static bool IsForbiddenHost(Uri uri)
    {
        if (!System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    public void SetRoots(IEnumerable<string> rootUrls)
    {
        var list = new List<Uri>();
        foreach (var url in rootUrls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) &&
                (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) &&
                string.IsNullOrEmpty(u.UserInfo) && !IsForbiddenHost(u))
            {
                list.Add(u);
            }
        }
        _roots = list.ToArray();
    }

    public async Task RefreshAsync(IDbContextFactory<AppDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var urls = await db.Sources.AsNoTracking().Select(s => s.RootUrl).ToListAsync(ct);
        SetRoots(urls);
    }

    public bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && IsAllowed(u);

    public bool IsAllowed(Uri uri) => IsUnder(uri, _roots);

    /// <summary>Allowed AND located under one specific source root (used while crawling that source).</summary>
    public bool IsAllowedUnder(Uri uri, Uri sourceRoot) => IsAllowed(uri) && IsUnder(uri, new[] { sourceRoot });

    private static bool IsUnder(Uri uri, IEnumerable<Uri> roots)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || IsForbiddenHost(uri)) return false;

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.Split('/').Any(seg => seg == "..")) return false;

        foreach (var root in roots)
        {
            if (!string.Equals(root.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(root.Host, uri.Host, StringComparison.OrdinalIgnoreCase)) continue;
            if (root.Port != uri.Port) continue;

            var rootPath = Uri.UnescapeDataString(root.AbsolutePath);
            if (!rootPath.EndsWith('/')) rootPath += "/";
            if (path.StartsWith(rootPath, StringComparison.Ordinal) || path + "/" == rootPath) return true;
        }
        return false;
    }
}
