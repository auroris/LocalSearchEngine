using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Extraction;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// Decides, before a run composes, whether the site's own feeds prove the change list complete —
/// the positive-indicator contract. For each seed origin it fetches the root page, takes the first
/// in-scope feed the page advertises, and walks the feed's entries newest-first: entries not yet
/// covered are the changes to crawl, and the first entry we've already covered (our stored visit is
/// at or after the entry's own date) is the boundary proving everything older was seen too. Every
/// origin bounding this way means the run can be exactly the collected changes and then stop.
///
/// Any origin that can't prove it — no advertised feed, an unparseable one, or a feed whose window
/// ends before a covered entry — forfeits incremental for the whole run: an exhausted window means
/// there may be older changes the feed no longer lists, exactly the case where only a full crawl is
/// honest. The covered test is date-based on purpose: an edited old post reappearing at the top of
/// the feed with a fresh date must count as a change, which a mere "URL is in the index" test would
/// stop on and silently skip. Entries with no parseable date can never prove coverage, so date-less
/// feeds degrade to the full-crawl fallback rather than to guessing.
/// </summary>
internal sealed class IncrementalPlanner
{
    private readonly HttpClient _httpClient;
    private readonly string _connectionString;
    private readonly long _maxBytes;
    private readonly ILogger _logger;

    public IncrementalPlanner(HttpClient httpClient, string connectionString, long maxBytes, ILogger logger)
    {
        _httpClient = httpClient;
        _connectionString = connectionString;
        _maxBytes = maxBytes;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to plan an incremental run over the given seed origins by autodiscovery: each
    /// origin's root page must advertise a feed, and each feed must bound its own origin.
    /// </summary>
    /// <param name="seeds">The seed root URLs whose origins the run covers.</param>
    /// <param name="scope">The run's host rules; out-of-scope feeds and entries are ignored.</param>
    /// <param name="ct">Cancels the probes.</param>
    /// <returns>The changed items to crawl (possibly empty: the feeds prove nothing changed), or
    /// <c>null</c> when any origin could not prove its change list complete and a full crawl is
    /// required.</returns>
    public async Task<IReadOnlyList<Uri>?> TryPlanAsync(IReadOnlyList<Uri> seeds, AllowedHosts scope, CancellationToken ct = default)
    {
        var changed = new List<Uri>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var read = new SqliteConnection(_connectionString);
        await read.OpenAsync(ct);

        foreach (var seed in seeds)
        {
            var feedUri = await DiscoverAdvertisedFeedAsync(seed, scope, ct);
            if (feedUri is null)
            {
                _logger.LogInformation("Incremental: {Seed} advertises no usable feed; a full crawl is required.", seed);
                return null;
            }

            if (!await WalkFeedAsync(read, feedUri, scope, changed, seenKeys, ct))
            {
                return null;
            }
        }

        return changed;
    }

    /// <summary>
    /// Attempts to plan an incremental run from one declared change-journal feed that covers every
    /// host in the run at once — the configuration for a site set where only one host can serve a
    /// feed (a document server has no page to advertise one on). The same boundary rule applies to
    /// the single feed: entries above the first already-covered one are the whole run's changes.
    /// A journal shaped as "everything in the window plus a tail of the most recently changed items
    /// before it" is self-anchoring — on a quiet day the tail is the immediate boundary, and after
    /// missed runs a fully-stale tail forces the full-crawl fallback all by itself.
    /// </summary>
    /// <param name="feedUri">The configured journal feed.</param>
    /// <param name="scope">The run's host rules; out-of-scope entries are ignored.</param>
    /// <param name="ct">Cancels the probes.</param>
    /// <returns>The changed items to crawl (possibly empty), or <c>null</c> when the feed could not
    /// be fetched, parsed, or bounded — a full crawl is required.</returns>
    public async Task<IReadOnlyList<Uri>?> TryPlanDeclaredAsync(Uri feedUri, AllowedHosts scope, CancellationToken ct = default)
    {
        var changed = new List<Uri>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var read = new SqliteConnection(_connectionString);
        await read.OpenAsync(ct);

        return await WalkFeedAsync(read, feedUri, scope, changed, seenKeys, ct) ? changed : null;
    }

    /// <summary>
    /// Fetches one feed and walks its entries newest-first: uncovered entries join
    /// <paramref name="changed"/>, and the first covered entry (stored visit at or after the
    /// entry's own date) bounds the walk.
    /// </summary>
    /// <returns><c>true</c> when the feed bounded; <c>false</c> means a full crawl is required.</returns>
    private async Task<bool> WalkFeedAsync(
        SqliteConnection read, Uri feedUri, AllowedHosts scope,
        List<Uri> changed, HashSet<string> seenKeys, CancellationToken ct)
    {
        var items = await FetchFeedItemsAsync(feedUri, ct);
        if (items is null)
        {
            _logger.LogInformation("Incremental: feed {Feed} could not be fetched or parsed; a full crawl is required.", feedUri);
            return false;
        }

        bool bounded = false;
        int feedChanged = 0;
        foreach (var item in items)
        {
            if (!scope.IsAllowed(item.Location))
            {
                continue; // not crawlable either way; neither a change nor a boundary
            }

            var key = UrlNormalizer.Normalize(item.Location);
            var lastCrawled = await CrawlStore.GetLastCrawledAsync(read, key);
            if (item.PublishedUtc is DateTime published && lastCrawled is DateTime covered && covered >= published)
            {
                bounded = true;
                break;
            }

            if (seenKeys.Add(key))
            {
                changed.Add(item.Location);
                feedChanged++;
            }
        }

        if (!bounded)
        {
            _logger.LogInformation(
                "Incremental: feed {Feed} ended without reaching an already-covered entry; assuming unlisted changes and requiring a full crawl.", feedUri);
            return false;
        }

        _logger.LogInformation("Incremental: feed {Feed} proves {Count} changed item(s).", feedUri, feedChanged);
        return true;
    }

    /// <summary>Fetches the seed's root page and returns the first in-scope feed it advertises.</summary>
    private async Task<Uri?> DiscoverAdvertisedFeedAsync(Uri seed, AllowedHosts scope, CancellationToken ct)
    {
        var body = await FetchAsync(seed, ct);
        if (body is null) return null;

        var analysis = ContentExtractor.AnalyzeHtml(
            body, httpCharset: null, xRobotsTag: null, UrlNormalizer.Normalize(seed),
            scope, new Dictionary<string, RobotsRules>(), CrawlerService.UserAgent);
        foreach (var feed in analysis.AdvertisedFeedUris)
        {
            if (scope.IsAllowed(feed))
            {
                return feed;
            }
        }
        return null;
    }

    /// <summary>Fetches and parses a feed, or <c>null</c> when it can't be had or read.</summary>
    private async Task<List<FeedItem>?> FetchFeedItemsAsync(Uri feedUri, CancellationToken ct)
    {
        var body = await FetchAsync(feedUri, ct);
        if (body is null) return null;
        return FeedParser.TryParse(body, feedUri, out var items) ? items : null;
    }

    /// <summary>One size-capped probe GET; any failure is a "can't prove it" answer, never an exception.</summary>
    private async Task<byte[]?> FetchAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = HttpContentReader.NewRequestTimeout();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        try
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode) return null;
            var (body, truncated) = await HttpContentReader.ReadLimitedAsync(response, _maxBytes, linked.Token);
            return truncated ? null : body;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Incremental probe failed for {Url}", uri);
            return null;
        }
    }
}
