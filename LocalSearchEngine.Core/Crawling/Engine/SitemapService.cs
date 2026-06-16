using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// Fetches, parses, and enqueues URLs discovered in host XML sitemaps.
/// </summary>
internal sealed class SitemapService
{
    /// <summary>The HTTP client used to fetch sitemap files.</summary>
    private readonly HttpClient _httpClient;
    /// <summary>The logger instance.</summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SitemapService"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to send requests.</param>
    /// <param name="logger">The logger instance.</param>
    public SitemapService(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Discovers sitemaps for the seed's origin and enqueues the entries that live on that origin.
    /// </summary>
    /// <param name="originUri">The base URI of the origin seed.</param>
    /// <param name="context">The active crawl context.</param>
    /// <param name="robots">The robots.txt rules which may contain sitemap declarations.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EnqueueSitemapUrlsAsync(Uri originUri, CrawlContext context, RobotsRules robots, CancellationToken cancellationToken)
    {
        var originKey = UrlOrigin.Key(originUri);

        var pending = new Queue<string>();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sitemap in robots.Sitemaps) pending.Enqueue(sitemap);
        pending.Enqueue(new Uri(originUri, "/sitemap.xml").ToString());

        int added = 0;
        int safety = 0;
        while (pending.Count > 0 && safety++ < 200)
        {
            var sitemapUrl = pending.Dequeue();
            if (!processed.Add(sitemapUrl)) continue;
            if (!Uri.TryCreate(sitemapUrl, UriKind.Absolute, out var sitemapUri) || !context.AllowedHosts.IsAllowed(sitemapUri))
            {
                _logger.LogInformation("Skipping out-of-scope sitemap: {Url}", sitemapUrl);
                continue;
            }

            var (locations, nestedSitemaps) = await FetchSitemapAsync(sitemapUrl, context.MaxCrawlSizeBytes, cancellationToken);

            foreach (var nested in nestedSitemaps)
            {
                if (!processed.Contains(nested)) pending.Enqueue(nested);
            }

            foreach (var loc in locations)
            {
                if (!UrlNormalizer.TryNormalize(loc, out var normalizedUrl)) continue;
                if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var locUri)) continue;

                if (!string.Equals(UrlOrigin.Key(locUri), originKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (!CrawlPolicy.IsAllowedByRobots(normalizedUrl, robots)) continue;

                if (context.Discover(normalizedUrl))
                {
                    added++;
                }
            }
        }

        if (added > 0)
        {
            _logger.LogInformation("Enqueued {Count} URLs from sitemaps for {Origin}", added, originKey);
        }
    }

    /// <summary>
    /// Fetches a sitemap XML document and parses its loc entries (including handling sitemap indexes recursively).
    /// </summary>
    /// <param name="sitemapUrl">The target sitemap URL to fetch.</param>
    /// <param name="maxBytes">The maximum allowed download size in bytes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing lists of parsed resource URLs (Locations) and nested sitemap URLs (NestedSitemaps).</returns>
    private async Task<(List<string> Locations, List<string> NestedSitemaps)> FetchSitemapAsync(string sitemapUrl, long maxBytes, CancellationToken cancellationToken)
    {
        var locations = new List<string>();
        var nested = new List<string>();
        try
        {
            using var response = await _httpClient.GetAsync(sitemapUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return (locations, nested);

            if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
            {
                _logger.LogWarning("Skipping sitemap {Url}: Content-Length ({Length} bytes) exceeds the {Limit}-byte limit.", sitemapUrl, declaredLength, maxBytes);
                return (locations, nested);
            }

            var (bytes, truncated) = await ReadBodyLimitedAsync(response, maxBytes, cancellationToken);
            if (truncated)
            {
                _logger.LogWarning("Skipping sitemap {Url}: body exceeds the {Limit}-byte limit.", sitemapUrl, maxBytes);
                return (locations, nested);
            }

            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            var doc = new XmlDocument { XmlResolver = null };
            using (var byteStream = new MemoryStream(bytes))
            using (var xmlReader = XmlReader.Create(byteStream, readerSettings))
            {
                doc.Load(xmlReader);
            }

            bool isIndex = string.Equals(doc.DocumentElement?.LocalName, "sitemapindex", StringComparison.OrdinalIgnoreCase);
            foreach (XmlNode node in doc.GetElementsByTagName("loc"))
            {
                var value = node.InnerText?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                if (isIndex) nested.Add(value); else locations.Add(value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch or parse sitemap {Url} (it may not exist)", sitemapUrl);
        }

        return (locations, nested);
    }

    /// <summary>
    /// Reads response stream up to a byte limit, returning the read byte array and whether it was truncated.
    /// </summary>
    /// <param name="response">The HTTP response message.</param>
    /// <param name="maxBytes">The maximum bytes to read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the read byte array and a boolean indicating if reading was truncated.</returns>
    private static async Task<(byte[] Body, bool Truncated)> ReadBodyLimitedAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bodyStream = new MemoryStream();
        var buffer = new byte[8192];
        while (bodyStream.Length < maxBytes)
        {
            int toRead = (int)Math.Min(buffer.Length, maxBytes - bodyStream.Length);
            int bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (bytesRead == 0) return (bodyStream.ToArray(), false);
            bodyStream.Write(buffer, 0, bytesRead);
        }
        bool truncated = await responseStream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) > 0;
        return (bodyStream.ToArray(), truncated);
    }
}
