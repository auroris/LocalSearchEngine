using System.Xml;
using Microsoft.Extensions.Logging;

namespace LocalSearchEngine.Core.Crawling;

// Discovery half of CrawlerService: fetching robots.txt and walking sitemaps to seed the frontier.
// Split from the crawl-loop orchestration purely for readability — these remain instance members of
// the same class and share its HttpClient, logger, and CrawlContext.
/// <summary>
/// Orchestrates a crawl: a single producer fetches and parses pages, handing units of work to a single indexer.
/// </summary>
public partial class CrawlerService
{
    /// <summary>
    /// Fetches and parses the robots.txt file for a given host URL.
    /// </summary>
    /// <param name="baseUri">The base URI of the host.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="RobotsRules"/> instance containing the parsed rules.</returns>
    private async Task<RobotsRules> GetRobotsRulesAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            var robotsUrl = new Uri(baseUri, "/robots.txt");
            using var response = await _httpClient.GetAsync(robotsUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return RobotsRules.Parse(content, UserAgent);
            }

            // RFC 9309 §2.3.1.3/§2.3.1.4: a 4xx (e.g. no robots.txt) means "no restrictions",
            // but a 5xx means the site is telling us it's unavailable — treat it as disallow-all
            // rather than hammering a struggling server.
            if ((int)response.StatusCode >= 500)
            {
                _logger.LogWarning("robots.txt for {Host} returned {Status}; treating as disallow-all.", baseUri.Host, (int)response.StatusCode);
                return RobotsRules.DisallowAll;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch or parse robots.txt");
        }
        return RobotsRules.AllowAll;
    }

    /// <summary>
    /// Discovers sitemaps and enqueues sitemap location URLs into the crawl context queue.
    /// </summary>
    /// <param name="hostUri">The host base URI.</param>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task EnqueueSitemapUrlsAsync(Uri hostUri, CrawlContext ctx, CancellationToken cancellationToken)
    {
        var robots = ctx.RobotsCache.TryGetValue(hostUri.Host, out var r) ? r : RobotsRules.AllowAll;

        // Seed from robots.txt Sitemap: directives plus the conventional location.
        var pending = new Queue<string>();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sitemap in robots.Sitemaps) pending.Enqueue(sitemap);
        pending.Enqueue(new Uri(hostUri, "/sitemap.xml").ToString());

        int added = 0;
        int safety = 0;
        while (pending.Count > 0 && safety++ < 200)
        {
            var sitemapUrl = pending.Dequeue();
            if (!processed.Add(sitemapUrl)) continue;

            var (locations, nestedSitemaps) = await FetchSitemapAsync(sitemapUrl, cancellationToken);

            foreach (var nested in nestedSitemaps)
            {
                if (!processed.Contains(nested)) pending.Enqueue(nested);
            }

            foreach (var loc in locations)
            {
                if (!UrlNormalizer.TryNormalize(loc, out var normalizedUrl)) continue;
                if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var locUri)) continue;
                if (!ctx.AllowedHosts.Contains(locUri.Host)) continue;
                if (!CrawlPolicy.IsIndexableExtension(normalizedUrl)) continue;

                // Check each entry against ITS OWN host's robots, not the sitemap's host.
                var locRobots = ctx.RobotsCache.TryGetValue(locUri.Host, out var lr) ? lr : RobotsRules.AllowAll;
                if (!CrawlPolicy.IsAllowedByRobots(normalizedUrl, locRobots)) continue;

                if (ctx.Visited.Add(normalizedUrl))
                {
                    ctx.Queue.Enqueue(normalizedUrl);
                    added++;
                }
            }
        }

        if (added > 0)
        {
            _logger.LogInformation("Enqueued {Count} URLs from sitemaps for {Host}", added, hostUri.Host);
        }
    }

    /// <summary>
    /// Fetches a sitemap XML and extracts URL locations and nested sitemaps.
    /// </summary>
    /// <param name="sitemapUrl">The URL of the sitemap XML.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing lists of locations and nested sitemap URLs.</returns>
    private async Task<(List<string> Locations, List<string> NestedSitemaps)> FetchSitemapAsync(string sitemapUrl, CancellationToken cancellationToken)
    {
        var locations = new List<string>();
        var nested = new List<string>();
        try
        {
            using var response = await _httpClient.GetAsync(sitemapUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return (locations, nested);

            // The crawler's HttpClient enables AutomaticDecompression, so transport-level
            // gzip/deflate/brotli (Content-Encoding) is already undone by the time we read the
            // body. We deliberately do NOT special-case ".gz" sitemap files served as raw gzip
            // bodies — uncommon for the sites this crawls, and dropping it keeps the path simple.
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            // Parse with external entities and DTDs disabled so a hostile sitemap can't trigger
            // entity expansion or out-of-band fetches (XXE).
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
}
