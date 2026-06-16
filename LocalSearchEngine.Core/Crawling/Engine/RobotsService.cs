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

        var (rules, unavailable) = await GetRobotsRulesAsync(UrlOrigin.BaseUri(uri), context, cancellationToken);

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

                    // NOTE: Writes directly to context.Write are safe here because this post-crawl cleanup phase
                    // runs after the main crawl consumer task has fully completed.
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
    /// Performs the HTTP fetch of robots.txt for the given base URI, recording host reachability and
    /// returning parse results or policy fallbacks.
    /// </summary>
    /// <param name="baseUri">The base URI/origin host to request robots.txt from.</param>
    /// <param name="context">The active crawl context (supplies the size limit and host-health tracker).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the parsed rules and whether robots.txt was unavailable (5xx error).</returns>
    private async Task<(RobotsRules Rules, bool Unavailable)> GetRobotsRulesAsync(Uri baseUri, CrawlContext context, CancellationToken cancellationToken)
    {
        try
        {
            var robotsUrl = new Uri(baseUri, "/robots.txt");
            using var response = await _httpClient.GetAsync(robotsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            context.HostHealth.RecordResponse(baseUri.Host);

            if (response.IsSuccessStatusCode)
            {
                var (body, truncated) = await HttpContentReader.ReadLimitedAsync(response, context.MaxCrawlSizeBytes, cancellationToken);
                if (truncated)
                {
                    _logger.LogWarning("robots.txt for {Host} exceeds the {Limit}-byte limit; parsing the truncated prefix.", baseUri.Host, context.MaxCrawlSizeBytes);
                }
                return (RobotsRules.Parse(Encoding.UTF8.GetString(body), CrawlerService.UserAgent), false);
            }

            if ((int)response.StatusCode >= 500)
            {
                _logger.LogWarning("robots.txt for {Host} returned {Status}; treating as disallow-all.", baseUri.Host, (int)response.StatusCode);
                return (RobotsRules.DisallowAll, true);
            }

            return (RobotsRules.AllowAll, false);
        }
        catch (Exception ex)
        {
            if (context.HostHealth.RecordFailure(baseUri.Host, ex, cancellationToken))
            {
                _logger.LogWarning("Host {Host} is unreachable on first contact; writing it off and skipping its URLs for the rest of this run.", baseUri.Host);
            }
            _logger.LogWarning(ex, "Failed to fetch or parse robots.txt for {Host}.", baseUri.Host);
            return (RobotsRules.AllowAll, false);
        }
    }
}
