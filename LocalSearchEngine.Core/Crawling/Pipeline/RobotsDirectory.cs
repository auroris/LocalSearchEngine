using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;
using LocalSearchEngine.Core.Searching;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// The run's robots.txt authority. Each origin is fetched exactly once no matter how many workers
/// ask at once — the cache is a per-origin <see cref="Lazy{T}"/> single-flight, which is load-bearing
/// beyond politeness: host health writes a dead host off after its <em>first</em> connection failure,
/// and that contract only holds if a burst of workers can't race four fetches at it. Fetch outcomes
/// feed the host-health tracker; a 5xx marks the origin's rules unavailable, which exempts its URLs
/// from pruning (we can't tell what it would have disallowed).
/// </summary>
internal sealed class RobotsDirectory
{
    private readonly HttpClient _httpClient;
    private readonly HostHealthTracker _hostHealth;
    private readonly long _maxBytes;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<RobotsRules>>> _flights = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RobotsRules> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _unavailable = new(StringComparer.OrdinalIgnoreCase);

    public RobotsDirectory(HttpClient httpClient, HostHealthTracker hostHealth, long maxBytes, ILogger logger)
    {
        _httpClient = httpClient;
        _hostHealth = hostHealth;
        _maxBytes = maxBytes;
        _logger = logger;
    }

    /// <summary>Gets the live view of rules fetched so far, keyed by origin — what HTML analysis filters links against.</summary>
    public IReadOnlyDictionary<string, RobotsRules> Rules => _rules;

    /// <summary>Whether the origin's robots.txt was unavailable (5xx) this run; its URLs are exempt from pruning.</summary>
    /// <param name="originKey">The origin key (scheme://host:port).</param>
    public bool IsUnavailable(string originKey) => _unavailable.ContainsKey(originKey);

    /// <summary>
    /// Returns the origin's robots rules, fetching them on first contact. Concurrent callers for the
    /// same origin share one fetch.
    /// </summary>
    /// <param name="uri">Any URL on the origin.</param>
    public Task<RobotsRules> GetOrFetchAsync(Uri uri)
    {
        var origin = UrlOrigin.Key(uri);
        var flight = _flights.GetOrAdd(origin, key => new Lazy<Task<RobotsRules>>(
            () => FetchAsync(key, UrlOrigin.BaseUri(uri)),
            LazyThreadSafetyMode.ExecutionAndPublication));
        return flight.Value;
    }

    private async Task<RobotsRules> FetchAsync(string origin, Uri baseUri)
    {
        var (rules, unavailable) = await GetRobotsRulesAsync(baseUri);
        if (unavailable)
        {
            _unavailable.TryAdd(origin, 0);
        }
        _rules[origin] = rules;
        return rules;
    }

    /// <summary>
    /// Performs the HTTP fetch of robots.txt, recording host reachability and returning parse
    /// results or policy fallbacks: 2xx parses (truncated past the size cap parses the prefix),
    /// 5xx is disallow-all and unavailable, any other status allows all, and a transport failure
    /// allows all while feeding the host-health write-off.
    /// </summary>
    private async Task<(RobotsRules Rules, bool Unavailable)> GetRobotsRulesAsync(Uri baseUri)
    {
        using var timeout = HttpContentReader.NewRequestTimeout(_httpClient);
        try
        {
            var robotsUrl = new Uri(baseUri, "/robots.txt");
            using var response = await _httpClient.GetAsync(robotsUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            _hostHealth.RecordResponse(baseUri.Host);

            if (response.IsSuccessStatusCode)
            {
                var (body, truncated) = await HttpContentReader.ReadLimitedAsync(response, _maxBytes, timeout.Token);
                if (truncated)
                {
                    _logger.LogWarning("robots.txt for {Host} exceeds the {Limit}-byte limit; parsing the truncated prefix.", baseUri.Host, _maxBytes);
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
            if (timeout.IsCancellationRequested)
            {
                _logger.LogWarning("robots.txt request for {Host} timed out after {Seconds}s.", baseUri.Host, (int)_httpClient.Timeout.TotalSeconds);
            }
            if (_hostHealth.RecordFailure(baseUri.Host, ex))
            {
                _logger.LogWarning("Host {Host} is unreachable on first contact; writing it off and skipping its URLs for the rest of this run.", baseUri.Host);
            }
            _logger.LogWarning(ex, "Failed to fetch or parse robots.txt for {Host}.", baseUri.Host);
            return (RobotsRules.AllowAll, false);
        }
    }

    /// <summary>
    /// Removes already-indexed URLs that an origin's robots.txt fetched this run now disallows.
    /// Runs only after the persistence consumer has drained — its writes are ungated, and that
    /// post-drain timing is what keeps the single-writer rule intact.
    /// </summary>
    /// <param name="read">The orchestrator's read connection.</param>
    /// <param name="write">The orchestrator's write connection.</param>
    /// <param name="vectorSearchService">Deletes the banned URLs' chunks.</param>
    /// <param name="observer">Receives the failure event if the pass dies.</param>
    /// <returns>The number of banned URLs removed.</returns>
    public async Task<int> RemoveBannedUrlsAsync(
        SqliteConnection read, SqliteConnection write,
        VectorSearchService vectorSearchService, ICrawlObserver observer)
    {
        int removed = 0;
        try
        {
            foreach (var (origin, rules) in _rules)
            {
                if (_unavailable.ContainsKey(origin)) continue;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) continue;
                if (_hostHealth.IsUnreachable(originUri.Host)) continue;

                var candidates = await CrawlStore.GetCrawledUrlsWithPrefixAsync(
                    read, originUri.GetLeftPart(UriPartial.Authority));
                foreach (var url in candidates)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                    if (!string.Equals(UrlOrigin.Key(uri), origin, StringComparison.OrdinalIgnoreCase)) continue;
                    if (CrawlPolicy.IsAllowedByRobots(url, rules)) continue;

                    await vectorSearchService.DeleteUrlChunksAsync(url);
                    await CrawlStore.DeleteLinksAsync(write, url);
                    await CrawlStore.DeleteCrawlStateAsync(write, url);
                    removed++;
                }
            }
        }
        catch (Exception ex)
        {
            observer.OnRemoveBannedFailed(ex);
        }
        return removed;
    }
}
