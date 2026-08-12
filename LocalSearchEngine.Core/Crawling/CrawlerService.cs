using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Pipeline;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Orchestrates a crawl: composes a <see cref="CrawlPlan"/> from the requested options, runs it on
/// the channel <see cref="CrawlPipeline"/> (N crawl workers feeding one persistence consumer), then
/// runs the post-crawl passes — robots-ban removal, stale pruning, internal PageRank, database
/// optimization, and link verification — which write ungated only because they start after the
/// pipeline's single writer has drained. The public surface is the stable facade: how a run behaves
/// is decided entirely by the plan's composition of seed sources and policy flags.
/// </summary>
public partial class CrawlerService
{
    /// <summary>Identifies this crawler in request headers and for robots.txt matching.</summary>
    public const string UserAgent = "LocalSearchEngine-Bot/1.0";

    static CrawlerService()
    {
        // Register legacy code-page encodings (windows-1252, etc.) so a sitemap's XML declaration
        // can resolve them.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly HttpClient _httpClient;
    /// <summary>The vector search service for storing and indexing embeddings.</summary>
    private readonly VectorSearchService _vectorSearchService;
    /// <summary>The logger instance for this service.</summary>
    private readonly ILogger<CrawlerService> _logger;
    /// <summary>The SQLite connection string.</summary>
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrawlerService"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client used for web requests.</param>
    /// <param name="vectorSearchService">The vector search service provider.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="dbConfig">The configuration specifying connection settings.</param>
    public CrawlerService(HttpClient httpClient, VectorSearchService vectorSearchService, ILogger<CrawlerService> logger, DatabaseConfig dbConfig)
    {
        _httpClient = httpClient;
        _vectorSearchService = vectorSearchService;
        _logger = logger;
        _connectionString = dbConfig.ConnectionString;
    }

    /// <summary>
    /// Creates the crawl tables and FTS mirror. VectorSearchService.EnsureCreatedAsync() must run first.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous schema creation.</returns>
    public Task EnsureCreatedAsync() => CrawlStore.EnsureSchemaAsync(_connectionString);

    /// <summary>
    /// Runs a full crawl starting from a seed URL: the seed origin's sitemaps plus the seed itself
    /// feed the frontier, links are followed, and on natural completion unreached URLs are pruned.
    /// </summary>
    /// <param name="seedUrl">The starting URL of the crawl.</param>
    /// <param name="maxPages">The maximum number of pages to index.</param>
    /// <param name="allowedServers">Optional additional allowed hosts.</param>
    /// <param name="noIndexPatterns">Optional URL glob patterns whose pages are followed for links but never indexed ("noindex, follow").</param>
    /// <param name="maxPagesPerHost">The maximum pages to crawl on any single host.</param>
    /// <param name="maxCrawlSizeBytes">The maximum size in bytes allowed for a crawled page/file.</param>
    /// <param name="checkExternalLinks">Whether to check external links after the crawl.</param>
    /// <param name="reporter">Receives live progress and phase changes.</param>
    /// <param name="requestDelayMs">The politeness gap between same-host request starts when robots.txt declares no crawl-delay.</param>
    /// <param name="crawlWorkers">The number of concurrent crawl workers (the politeness gap still bounds how often any one host is contacted).</param>
    /// <returns>A <see cref="CrawlReport"/> summarizing what the crawl indexed, removed, and discovered.</returns>
    public Task<CrawlReport> CrawlAsync(
        string seedUrl,
        int maxPages = int.MaxValue,
        IEnumerable<string>? allowedServers = null,
        IEnumerable<string>? noIndexPatterns = null,
        int maxPagesPerHost = int.MaxValue,
        long maxCrawlSizeBytes = 15 * 1024 * 1024,
        bool checkExternalLinks = false,
        ICrawlReporter? reporter = null,
        int requestDelayMs = 250,
        int crawlWorkers = 4)
    {
        return RunAsync(new[] { seedUrl }, RunMode.FullSites, incrementalFeed: null, maxPages, allowedServers,
            noIndexPatterns, maxPagesPerHost, maxCrawlSizeBytes, checkExternalLinks, reporter, requestDelayMs, crawlWorkers);
    }

    /// <summary>
    /// Crawls one or more configured sites in a single run — the no-argument CLI path, seeded from
    /// every allowed origin at once so post-crawl pruning sees the whole in-scope world in one pass.
    /// With <paramref name="allowIncremental"/>, each site's advertised feed is consulted first as a
    /// positive indicator: walking it newest-first, entries not yet covered are the changes, and the
    /// first already-covered entry (stored visit at or after the entry's date) proves everything
    /// older was seen — so the run crawls exactly those changes and stops. If any site can't prove
    /// its change list complete (no feed, or the feed's window ends before a covered entry), the
    /// run falls back to a normal full crawl of every site.
    /// </summary>
    /// <param name="seedUrls">The site root URLs to crawl.</param>
    /// <param name="allowIncremental">Whether feeds may bound the run to just the listed changes.</param>
    /// <param name="incrementalFeed">A declared change-journal feed covering every seed host at
    /// once; when set, it replaces per-site autodiscovery as the incremental proof. The site set's
    /// one feed-capable host can vouch for hosts (like a bare document server) that cannot
    /// advertise a feed of their own.</param>
    /// <param name="maxPages">The maximum number of pages to index.</param>
    /// <param name="allowedServers">Optional additional allowed hosts.</param>
    /// <param name="noIndexPatterns">Optional URL glob patterns whose pages are followed for links but never indexed.</param>
    /// <param name="maxPagesPerHost">The maximum pages to crawl on any single host.</param>
    /// <param name="maxCrawlSizeBytes">The maximum size in bytes allowed for a crawled page/file.</param>
    /// <param name="checkExternalLinks">Whether to check external links after a full crawl.</param>
    /// <param name="reporter">Receives live progress and phase changes.</param>
    /// <param name="requestDelayMs">The politeness gap between same-host request starts.</param>
    /// <param name="crawlWorkers">The number of concurrent crawl workers.</param>
    /// <returns>A <see cref="CrawlReport"/> summarizing the run.</returns>
    public Task<CrawlReport> CrawlSitesAsync(
        IReadOnlyList<string> seedUrls,
        bool allowIncremental = false,
        string? incrementalFeed = null,
        int maxPages = int.MaxValue,
        IEnumerable<string>? allowedServers = null,
        IEnumerable<string>? noIndexPatterns = null,
        int maxPagesPerHost = int.MaxValue,
        long maxCrawlSizeBytes = 15 * 1024 * 1024,
        bool checkExternalLinks = false,
        ICrawlReporter? reporter = null,
        int requestDelayMs = 250,
        int crawlWorkers = 4)
    {
        return RunAsync(seedUrls, allowIncremental ? RunMode.AutoSites : RunMode.FullSites,
            incrementalFeed, maxPages, allowedServers, noIndexPatterns, maxPagesPerHost,
            maxCrawlSizeBytes, checkExternalLinks, reporter, requestDelayMs, crawlWorkers);
    }

    /// <summary>
    /// Runs an update crawl driven by an RSS/Atom feed: the feed is trusted as the site's change
    /// journal, so only the items it lists are fetched (with conditional requests — unchanged items
    /// answer 304, and only a changed content hash re-embeds). Links are not followed, nothing is
    /// pruned, and the link-verification pass is skipped: the run touches exactly what the feed
    /// names and deletes nothing it didn't visit. Deletions on the site are reconciled by the next
    /// full <see cref="CrawlAsync"/>.
    /// </summary>
    /// <param name="feedUrl">The rss.xml / Atom feed URL.</param>
    /// <param name="maxPages">The maximum number of items to index.</param>
    /// <param name="allowedServers">Optional additional allowed hosts (item links outside scope are skipped).</param>
    /// <param name="noIndexPatterns">Optional URL glob patterns whose pages are fetched but never indexed.</param>
    /// <param name="maxCrawlSizeBytes">The maximum size in bytes allowed for a fetched page/file.</param>
    /// <param name="reporter">Receives live progress and phase changes.</param>
    /// <param name="requestDelayMs">The politeness gap between same-host request starts.</param>
    /// <param name="crawlWorkers">The number of concurrent crawl workers.</param>
    /// <returns>A <see cref="CrawlReport"/> summarizing what the update run indexed and touched.</returns>
    public Task<CrawlReport> CrawlFeedAsync(
        string feedUrl,
        int maxPages = int.MaxValue,
        IEnumerable<string>? allowedServers = null,
        IEnumerable<string>? noIndexPatterns = null,
        long maxCrawlSizeBytes = 15 * 1024 * 1024,
        ICrawlReporter? reporter = null,
        int requestDelayMs = 250,
        int crawlWorkers = 4)
    {
        return RunAsync(new[] { feedUrl }, RunMode.FeedUpdate, incrementalFeed: null, maxPages, allowedServers,
            noIndexPatterns, maxPagesPerHost: int.MaxValue, maxCrawlSizeBytes, checkExternalLinks: false,
            reporter, requestDelayMs, crawlWorkers);
    }

    /// <summary>How one run composes: a full sweep, a feed-may-bound-it sweep, or an explicit feed update.</summary>
    private enum RunMode
    {
        /// <summary>Sitemap + root seeds per site, links followed, prune on natural completion.</summary>
        FullSites,
        /// <summary>Like <see cref="FullSites"/>, unless every site's feed proves its change list complete — then exactly those changes.</summary>
        AutoSites,
        /// <summary>The seed is a feed; fetch exactly what it lists.</summary>
        FeedUpdate,
    }

    /// <summary>
    /// Composes and runs one crawl. Full crawls seed from sitemaps + the root URL of every site,
    /// follow links, prune on natural completion, and verify links; partial runs (an explicit feed
    /// update, or an incremental run the sites' feeds proved complete) do none of that — they fetch
    /// exactly the named items, index only what changed, and never delete anything the run didn't
    /// visit.
    /// </summary>
    private async Task<CrawlReport> RunAsync(
        IReadOnlyList<string> seedUrls,
        RunMode mode,
        string? incrementalFeed,
        int maxPages,
        IEnumerable<string>? allowedServers,
        IEnumerable<string>? noIndexPatterns,
        int maxPagesPerHost,
        long maxCrawlSizeBytes,
        bool checkExternalLinks,
        ICrawlReporter? reporter,
        int requestDelayMs,
        int crawlWorkers)
    {
        var crawlStartUtc = DateTime.UtcNow;
        reporter ??= NullCrawlReporter.Instance;
        var heartbeat = new CrawlHeartbeat();
        var observer = new CrawlObserver(_logger, reporter, crawlStartUtc, heartbeat);

        var seeds = new List<Uri>();
        foreach (var raw in seedUrls)
        {
            if (Uri.TryCreate(raw, UriKind.Absolute, out var seedUri))
            {
                seeds.Add(seedUri);
            }
            else
            {
                observer.OnSeedInvalid(raw);
            }
        }
        if (seeds.Count == 0)
        {
            return EmptyReport(seedUrls.Count > 0 ? seedUrls[0] : string.Empty, crawlStartUtc);
        }

        var scope = new AllowedHosts();
        if (allowedServers != null)
        {
            foreach (var s in allowedServers)
            {
                if (!scope.Add(s))
                {
                    observer.OnAllowedServerIgnored(s);
                }
            }
        }
        foreach (var seed in seeds)
        {
            scope.AddOrigin(seed);
        }

        var noIndexRules = new NoIndexRules();
        if (noIndexPatterns != null)
        {
            foreach (var p in noIndexPatterns)
            {
                if (!noIndexRules.Add(p))
                {
                    observer.OnNoIndexPatternIgnored(p);
                }
            }
        }

        observer.OnPhaseChanged(CrawlPhase.Starting);

        await using var readConnection = new SqliteConnection(_connectionString);
        await readConnection.OpenAsync();
        await using var writeConnection = new SqliteConnection(_connectionString);
        await writeConnection.OpenAsync();

        // Watchdog: logs a warning whenever one lane of the crawl has been on one activity too long,
        // so a stall shows up in the log with the URL/phase that's stuck instead of looking like a
        // frozen run.
        using var watchdogTimer = new PeriodicTimer(WatchdogInterval);
        var watchdogTask = RunStallWatchdogAsync(heartbeat, watchdogTimer);

        var (indexedUrlsAtStart, _) = await CrawlStore.GetCountsAsync(readConnection);

        var hostHealth = new HostHealthTracker();
        var robots = new RobotsDirectory(_httpClient, hostHealth, maxCrawlSizeBytes, _logger);
        var backlog = new EmbeddingBacklog();

        // First contact is each seed origin's robots.txt; a connection failure writes that host off
        // and drops its seed from the run.
        var reachableSeeds = new List<Uri>();
        foreach (var seed in seeds)
        {
            await robots.GetOrFetchAsync(seed);
            if (hostHealth.IsUnreachable(seed.Host))
            {
                observer.OnSeedUnreachable(seed.Host);
            }
            else
            {
                reachableSeeds.Add(seed);
            }
        }

        var result = new PipelineResult(0, 0, false, false);
        int discoveredCount = 0;
        string reportSeed = seedUrls[0];
        bool fullCrawl = mode != RunMode.FeedUpdate;

        if (reachableSeeds.Count > 0)
        {
            IReadOnlyList<ISeedSource> sources;
            if (mode == RunMode.FeedUpdate)
            {
                sources = new ISeedSource[] { new FeedSeedSource(reachableSeeds[0]) };
            }
            else
            {
                IReadOnlyList<Uri>? provenChanges = null;
                if (mode == RunMode.AutoSites)
                {
                    var planner = new IncrementalPlanner(_httpClient, _connectionString, maxCrawlSizeBytes, _logger);
                    if (incrementalFeed is not null)
                    {
                        // A declared journal covers the whole site set from one feed; misconfiguration
                        // is a full crawl, never a guess.
                        if (Uri.TryCreate(incrementalFeed, UriKind.Absolute, out var journalUri))
                        {
                            provenChanges = await planner.TryPlanDeclaredAsync(journalUri, scope);
                        }
                        else
                        {
                            _logger.LogWarning("incremental-feed '{Feed}' is not an absolute URL; running a full crawl.", incrementalFeed);
                        }
                    }
                    else
                    {
                        provenChanges = await planner.TryPlanAsync(reachableSeeds, scope);
                    }
                }

                if (provenChanges is not null)
                {
                    // Every site's feed proved its change list complete: the run is exactly those
                    // changes (possibly none), then stop — with all of a partial run's guarantees.
                    sources = new ISeedSource[] { new ListSeedSource(provenChanges) };
                    fullCrawl = false;
                }
                else
                {
                    var full = new List<ISeedSource>();
                    foreach (var seed in reachableSeeds)
                    {
                        full.Add(new SitemapSeedSource(seed, robots));
                    }
                    foreach (var seed in reachableSeeds)
                    {
                        full.Add(new RootUrlSource(seed, robots));
                    }
                    sources = full;
                }
            }

            var plan = new CrawlPlan
            {
                SeedUris = reachableSeeds,
                SeedSources = sources,
                Scope = scope,
                NoIndexRules = noIndexRules,
                FollowLinks = fullCrawl,
                PruneStale = fullCrawl,
                VerifyLinks = fullCrawl,
                CheckExternalLinks = checkExternalLinks,
                CrawlWorkers = crawlWorkers,
                MaxPages = maxPages,
                MaxPagesPerHost = maxPagesPerHost,
                MaxCrawlSizeBytes = maxCrawlSizeBytes,
                DefaultRequestDelayMs = requestDelayMs,
            };
            var pipeline = new CrawlPipeline(plan, _httpClient, _vectorSearchService, _connectionString,
                writeConnection, robots, hostHealth, observer, heartbeat, reporter, backlog, _logger);
            observer.DiscoveredCount = () => pipeline.Visited.Count;

            result = await pipeline.RunAsync();
            discoveredCount = pipeline.Visited.Count;
            reportSeed = UrlNormalizer.Normalize(reachableSeeds[0]);
        }

        bool completedNaturally = result.JobsSubmitted > 0
            && !result.HostCapSkipped
            && !result.CappedWithWorkRemaining;

        // The pipeline's single writer has drained; the passes below are the only writers from here,
        // running sequentially on this task — the single-writer rule holds without any gate.
        heartbeat.MarkCrawler(CrawlHeartbeat.Idle);

        // Feed runs are deliberately partial: even a robots.txt change must not delete historical
        // content the feed did not name. A subsequent full crawl reconciles both newly banned and
        // stale URLs once it has observed the whole site.
        if (fullCrawl)
        {
            observer.OnPhaseChanged(CrawlPhase.RemovingBanned);
            observer.OnBannedUrlsRemoved(await robots.RemoveBannedUrlsAsync(
                readConnection, writeConnection, _vectorSearchService, observer));
            if (completedNaturally)
            {
                observer.OnPhaseChanged(CrawlPhase.Pruning);
                observer.OnStaleUrlsPruned(await PruneStaleUrlsAsync(
                    readConnection, writeConnection, scope, robots, hostHealth, observer, crawlStartUtc));
            }
        }
        observer.OnPhaseChanged(CrawlPhase.Optimizing);

        try
        {
            var authority = await CrawlStore.RecomputePageRankAsync(writeConnection);
            _logger.LogInformation(
                "Computed internal PageRank for {Nodes} indexed URLs across {Edges} links in {Iterations} iterations.",
                authority.NodeCount, authority.EdgeCount, authority.Iterations);
        }
        catch (Exception ex)
        {
            // Authority refines search but must never turn a successful crawl into a failed one.
            _logger.LogWarning(ex, "Failed to recompute internal PageRank; existing authority scores were preserved.");
        }

        await CrawlStore.OptimizeDatabaseAsync(writeConnection, _logger);

        var linkVerifier = new LinkVerifier(_httpClient);
        var verification = new LinkVerificationContext(
            readConnection, writeConnection, scope, hostHealth, checkExternalLinks, observer, heartbeat);
        if (fullCrawl)
        {
            await linkVerifier.VerifyUndeterminedLinksAsync(verification, crawlStartUtc);
        }

        // The crawl and all its post-crawl passes are done; stop the watchdog before the final tally.
        watchdogTimer.Dispose();
        await watchdogTask;

        observer.OnPhaseChanged(CrawlPhase.Completed);

        var (indexedUrlsInDb, crawlStateRowsInDb) = await CrawlStore.GetCountsAsync(readConnection);
        long itemsDeleted = observer.Stats.Gone + observer.Stats.RemovedBanned + observer.Stats.RemovedStale;
        long itemsAdded = Math.Max(0, indexedUrlsInDb - indexedUrlsAtStart + itemsDeleted);

        var (brokenLinks, redirectedLinks) = await linkVerifier.BuildLinkReportAsync(verification, crawlStartUtc);

        observer.OnCrawlCompleted(seedUrls[0]);

        return new CrawlReport(
            reportSeed,
            crawlStartUtc,
            DateTime.UtcNow,
            completedNaturally,
            observer.Stats.Snapshot(CrawlPhase.Completed, discoveredCount, DateTime.UtcNow - crawlStartUtc),
            indexedUrlsInDb,
            crawlStateRowsInDb,
            itemsAdded,
            itemsDeleted,
            brokenLinks,
            redirectedLinks,
            hostHealth.UnreachableHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray(),
            backlog.Processed,
            backlog.Queued);
    }

    /// <summary>
    /// Removes index entries for in-scope URLs the completed crawl never reached. Only called for a
    /// full crawl that completed naturally: a capped, host-capped, or partial (feed) run hasn't seen
    /// everything, so "not visited" proves nothing.
    /// </summary>
    /// <param name="read">The orchestrator's read connection.</param>
    /// <param name="write">The orchestrator's write connection.</param>
    /// <param name="scope">The crawl's host rules; out-of-scope URLs are never pruned.</param>
    /// <param name="robots">Supplies the origins whose robots.txt was unavailable (their URLs are exempt).</param>
    /// <param name="hostHealth">The run's reachability tracker (unreachable hosts' URLs are exempt).</param>
    /// <param name="observer">Receives the failure event if the pass dies.</param>
    /// <param name="crawlStartUtc">The UTC timestamp indicating when the crawl started.</param>
    /// <returns>The number of stale URLs pruned from storage.</returns>
    private async Task<int> PruneStaleUrlsAsync(
        SqliteConnection read, SqliteConnection write, AllowedHosts scope,
        RobotsDirectory robots, HostHealthTracker hostHealth,
        ICrawlObserver observer, DateTime crawlStartUtc)
    {
        int pruned = 0;
        try
        {
            var candidates = await CrawlStore.GetUrlsNotCrawledSinceAsync(read, crawlStartUtc);
            foreach (var url in candidates)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                if (!scope.IsAllowed(uri)) continue;
                if (robots.IsUnavailable(UrlOrigin.Key(uri))) continue;
                if (hostHealth.IsUnreachable(uri.Host)) continue;

                await _vectorSearchService.DeleteUrlChunksAsync(url);
                await CrawlStore.DeleteLinksAsync(write, url);
                await CrawlStore.DeleteCrawlStateAsync(write, url);
                pruned++;
            }
        }
        catch (Exception ex)
        {
            observer.OnPruneFailed(ex);
        }
        return pruned;
    }

    /// <summary>
    /// Creates an empty crawl report in the event of an early failure or invalid input.
    /// </summary>
    /// <param name="seedUrl">The seed URL that failed validation or was unreachable.</param>
    /// <param name="startedUtc">The time the crawl was initiated.</param>
    /// <returns>An empty <see cref="CrawlReport"/>.</returns>
    private static CrawlReport EmptyReport(string seedUrl, DateTime startedUtc) => new(
        seedUrl, startedUtc, DateTime.UtcNow, false,
        new CrawlStats().Snapshot(CrawlPhase.Completed, 0, TimeSpan.Zero), 0, 0, 0, 0,
        Array.Empty<BrokenLink>(), Array.Empty<BrokenLink>(), Array.Empty<string>(), 0, 0);

    /// <summary>How often the stall watchdog samples the heartbeat.</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long one activity may run before the watchdog logs a possible stall.</summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Polls the heartbeat on a timer and logs a warning per lane whose current activity has been in
    /// flight past <see cref="StallThreshold"/>, naming the URL or phase that is stuck. Quiet lanes —
    /// parked idle or waiting out a host's politeness gap — are expected to sit and are never warned about.
    /// The loop ends when <paramref name="timer"/> is disposed.
    /// </summary>
    /// <param name="heartbeat">The shared activity marker every crawl actor bumps.</param>
    /// <param name="timer">The periodic timer driving the poll; disposing it stops the loop.</param>
    /// <returns>A <see cref="Task"/> that completes when the timer is disposed.</returns>
    private async Task RunStallWatchdogAsync(CrawlHeartbeat heartbeat, PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync())
        {
            foreach (var (lane, activity, elapsed) in heartbeat.Snapshot())
            {
                if (!CrawlHeartbeat.IsQuiet(activity) && elapsed >= StallThreshold)
                {
                    _logger.LogWarning("Possible stall: {Lane} '{Activity}' has been running for {Seconds}s.",
                        lane, activity, (int)elapsed.TotalSeconds);
                }
            }
        }
    }
}
