namespace LocalSearchEngine.Core.Crawling.Policies;

/// <summary>
/// Identifies the origin (scheme + host + port) of a URL. Robots.txt rules and sitemaps are
/// scoped per origin (RFC 9309), so caches key on this rather than the bare hostname — a site
/// on a non-default port gets its robots.txt fetched from that port, not port 80.
/// </summary>
public static class UrlOrigin
{
    /// <summary>
    /// Builds the cache key for a URL's origin, e.g. "http://example.com:8080".
    /// <see cref="Uri"/> lowercases the scheme and host and resolves the scheme's default
    /// port, so equivalent origins always produce the same key.
    /// </summary>
    /// <param name="uri">The absolute URI whose origin to key.</param>
    /// <returns>The origin key string.</returns>
    public static string Key(Uri uri) => $"{uri.Scheme}://{uri.Host}:{uri.Port}";

    /// <summary>
    /// Builds the base URI of a URL's origin, preserving its port, for fetching
    /// origin-scoped resources such as /robots.txt and /sitemap.xml.
    /// </summary>
    /// <param name="uri">The absolute URI whose origin base to build.</param>
    /// <returns>The origin's base <see cref="Uri"/> (path "/").</returns>
    public static Uri BaseUri(Uri uri) => new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri;
}
