using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// Fetches, caches, and enforces robots.txt rules and reachability policies.
/// </summary>
internal sealed class RobotsService
{
    /// <summary>The HTTP client used to fetch robots.txt files.</summary>
    private readonly HttpClient _httpClient;
    /// <summary>The vector search service provider, used here to prune banned URLs from embeddings.</summary>
    private readonly VectorSearchService _vectorSearchService;
    /// <summary>The logger instance.</summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RobotsService"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to send requests.</param>
    /// <param name="vectorSearchService">The vector search service provider.</param>
    /// <param name="logger">The logger instance.</param>
    public RobotsService(HttpClient httpClient, VectorSearchService vectorSearchService, ILogger logger)
    {
        _httpClient = httpClient;
        _vectorSearchService = vectorSearchService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the cached robots.txt rules for the URL's origin, fetching and caching them on
    /// first contact.
    /// </summary>
    /// <param name="uri">The target URL to retrieve robots.txt rules for.</param>
    /// <param name="context">The active crawl context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="RobotsRules"/> object containing the rules for the given origin.</returns>
    public async Task<RobotsRules> GetOrFetchRobotsAsync(Uri uri, CrawlContext context, CancellationToken cancellationToken)
    {
        var origin = UrlOrigin.Key(uri);
        if (context.RobotsCache.TryGetValue(origin, out var cached)) return cached;
        var (rules, unavailable, reachable) = await GetRobotsRulesAsync(UrlOrigin.BaseUri(uri), context.MaxCrawlSizeBytes, cancellationToken);

        if (reachable)
        {
            context.HostHealth.RecordContacted(uri.Host);
        }
        else if (context.HostHealth.RecordUnreachable(uri.Host))
        {
            _logger.LogWarning("Host {Host} did not respond on first contact; writing it off and skipping its URLs for the rest of this run.", uri.Host);
        }

        if (unavailable)
        {
            context.RobotsUnavailable.Add(origin);
        }
        context.RobotsCache[origin] = rules;
        return rules;
    }

    /// <summary>
    /// Removes already-indexed URLs that the robots.txt fetched for their origin this crawl now disallows.
    /// </summary>
    /// <param name="context">The active crawl context.</param>
    /// <returns>The number of banned URLs successfully removed from index and storage.</returns>
    public async Task<int> RemoveRobotsBannedUrlsAsync(CrawlContext context)
    {
        int removed = 0;
        try
        {
            foreach (var (origin, rules) in context.RobotsCache)
            {
                if (context.RobotsUnavailable.Contains(origin)) continue;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) continue;
                if (context.HostHealth.IsUnreachable(originUri.Host)) continue;

                var candidates = await CrawlStore.GetCrawledUrlsWithPrefixAsync(
                    context.Read, originUri.GetLeftPart(UriPartial.Authority), CancellationToken.None);
                foreach (var url in candidates)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                    if (!string.Equals(UrlOrigin.Key(uri), origin, StringComparison.OrdinalIgnoreCase)) continue;
                    if (CrawlPolicy.IsAllowedByRobots(url, rules)) continue;

                    await _vectorSearchService.DeleteUrlChunksAsync(url);
                    await CrawlStore.DeleteLinksAsync(context.Write, url, CancellationToken.None);
                    await CrawlStore.DeleteCrawlStateAsync(context.Write, url, CancellationToken.None);
                    removed++;
                }
            }
        }
        catch (Exception ex)
        {
            context.Observer.OnRemoveBannedFailed(ex);
        }
        return removed;
    }

    /// <summary>
    /// Performs the HTTP fetch of robots.txt for the given base URI, returning parse results or policy fallbacks.
    /// </summary>
    /// <param name="baseUri">The base URI/origin host to request robots.txt from.</param>
    /// <param name="maxBytes">The maximum allowed size of robots.txt to download.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the parsed rules, whether robots.txt was unavailable (5xx error), and whether the host was reachable.</returns>
    private async Task<(RobotsRules Rules, bool Unavailable, bool Reachable)> GetRobotsRulesAsync(Uri baseUri, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            var robotsUrl = new Uri(baseUri, "/robots.txt");
            using var response = await _httpClient.GetAsync(robotsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var (body, truncated) = await ReadBodyLimitedAsync(response, maxBytes, cancellationToken);
                if (truncated)
                {
                    _logger.LogWarning("robots.txt for {Host} exceeds the {Limit}-byte limit; parsing the truncated prefix.", baseUri.Host, maxBytes);
                }
                return (RobotsRules.Parse(Encoding.UTF8.GetString(body), CrawlerService.UserAgent), false, true);
            }

            if ((int)response.StatusCode >= 500)
            {
                _logger.LogWarning("robots.txt for {Host} returned {Status}; treating as disallow-all.", baseUri.Host, (int)response.StatusCode);
                return (RobotsRules.DisallowAll, true, true);
            }

            return (RobotsRules.AllowAll, false, true);
        }
        catch (Exception ex)
        {
            bool reachable = !PageDownloader.IsUnreachableError(ex, cancellationToken);
            _logger.LogWarning(ex, "Failed to fetch or parse robots.txt for {Host}.", baseUri.Host);
            return (RobotsRules.AllowAll, false, reachable);
        }
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
