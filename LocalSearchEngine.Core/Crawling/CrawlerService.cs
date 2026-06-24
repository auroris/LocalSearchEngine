using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;
using LocalSearchEngine.Core.Crawling.Engine;

namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Orchestrates a crawl: initializes the shared context, sets up connections, and coordinates a concurrent
/// crawler (CrawlProducer, which fetches and writes crawl state) and embedder (CrawlEmbedder, which embeds
/// and writes chunks off an unbounded queue), their writes serialized by a single DbWriteGate.
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
    /// Orchestrates the crawl loop starting from a seed URL.
    /// </summary>
    /// <param name="seedUrl">The starting URL of the crawl.</param>
    /// <param name="maxPages">The maximum number of pages to index.</param>
    /// <param name="allowedServers">Optional additional allowed hosts.</param>
    /// <param name="noIndexPatterns">Optional URL glob patterns whose pages are followed for links but never indexed ("noindex, follow").</param>
    /// <param name="maxPagesPerHost">The maximum pages to crawl on any single host.</param>
    /// <param name="maxCrawlSizeBytes">The maximum size in bytes allowed for a crawled page/file.</param>
    /// <param name="checkExternalLinks">Whether to check external links after the crawl.</param>
    /// <param name="reporter">Receives live progress and phase changes.</param>
    /// <returns>A <see cref="CrawlReport"/> summarizing what the crawl indexed, removed, and discovered.</returns>
    public async Task<CrawlReport> CrawlAsync(
        string seedUrl,
        int maxPages = int.MaxValue,
        IEnumerable<string>? allowedServers = null,
        IEnumerable<string>? noIndexPatterns = null,
        int maxPagesPerHost = int.MaxValue,
        long maxCrawlSizeBytes = 15 * 1024 * 1024,
        bool checkExternalLinks = false,
        ICrawlReporter? reporter = null)
    {
        var crawlStartUtc = DateTime.UtcNow;
        reporter ??= NullCrawlReporter.Instance;
        var heartbeat = new CrawlHeartbeat();
        var observer = new CrawlObserver(_logger, reporter, crawlStartUtc, heartbeat);

        if (!Uri.TryCreate(seedUrl, UriKind.Absolute, out var baseUri))
        {
            observer.OnSeedInvalid(seedUrl);
            return EmptyReport(seedUrl, crawlStartUtc);
        }

        var ctx = new CrawlContext
        {
            AllowedHosts = new AllowedHosts(),
            RobotsCache = new Dictionary<string, RobotsRules>(StringComparer.OrdinalIgnoreCase),
            Queue = new Queue<string>(),
            Visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            MaxCrawlSizeBytes = maxCrawlSizeBytes,
            CheckExternalLinks = checkExternalLinks,
            Observer = observer,
            Heartbeat = heartbeat,
            StartedUtc = crawlStartUtc,
        };

        // Feed the live frontier size (unique URLs discovered) into the observer's progress snapshots.
        observer.DiscoveredCount = () => ctx.Visited.Count;

        if (allowedServers != null)
        {
            foreach (var s in allowedServers)
            {
                if (!ctx.AllowedHosts.Add(s))
                {
                    ctx.Observer.OnAllowedServerIgnored(s);
                }
            }
        }
        ctx.AllowedHosts.AddOrigin(baseUri);

        if (noIndexPatterns != null)
        {
            foreach (var p in noIndexPatterns)
            {
                if (!ctx.NoIndexRules.Add(p))
                {
                    ctx.Observer.OnNoIndexPatternIgnored(p);
                }
            }
        }

        ctx.Observer.OnPhaseChanged(CrawlPhase.Starting);

        await using var readConnection = new SqliteConnection(_connectionString);
        await readConnection.OpenAsync();
        await using var writeConnection = new SqliteConnection(_connectionString);
        await writeConnection.OpenAsync();
        ctx.Read = readConnection;
        ctx.Write = writeConnection;

        // Watchdog: logs a warning whenever the crawl has been on one activity too long, so a stall
        // shows up in the log with the URL/phase that's stuck instead of looking like a frozen run.
        using var watchdogTimer = new PeriodicTimer(WatchdogInterval);
        var watchdogTask = RunStallWatchdogAsync(heartbeat, watchdogTimer);

        var (indexedUrlsAtStart, _) = await CrawlStore.GetCountsAsync(ctx.Read);

        // The crawl runs two threads. The crawler writes crawl-state/link rows and drops each page's chunk
        // work onto this unbounded queue; the embedder grinds through the queue, embedding text and writing
        // text_chunks. The queue is unbounded so the crawler never blocks on the (far slower) embedder and
        // finishes first; a single write gate serializes the two — SQLite permits one writer at a time.
        using var writeGate = new DbWriteGate();
        var embeddingChannel = Channel.CreateUnbounded<EmbeddingJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        // The embedder wraps every item in its own try/catch and only ends when the queue completes, so it
        // never faults out of its loop — every item the crawler enqueues is drained.
        var backlog = new EmbeddingBacklog();
        var embedder = new CrawlEmbedder(embeddingChannel.Reader, _vectorSearchService, writeGate, _logger, heartbeat, backlog, reporter);
        var embedderTask = embedder.ConsumeAsync();

        var crawlStateWriter = new CrawlStateWriter(ctx.Write, writeGate, _logger);

        var robotsService = new RobotsService(_httpClient, _vectorSearchService, _logger);
        var sitemapService = new SitemapService(_httpClient, _logger);
        var pageDownloader = new PageDownloader(_httpClient, _logger);
        var linkVerifier = new LinkVerifier(_httpClient);

        var producer = new CrawlProducer(_vectorSearchService, crawlStateWriter, embeddingChannel.Writer, backlog, ctx, robotsService, pageDownloader, _logger);
        var seedRobots = await robotsService.GetOrFetchRobotsAsync(baseUri, ctx);

        int producedJobs = 0;
        int indexedCount = 0;
        bool completedNaturally = false;

        if (ctx.HostHealth.IsUnreachable(baseUri.Host))
        {
            ctx.Observer.OnSeedUnreachable(baseUri.Host);
        }
        else
        {
            await sitemapService.EnqueueSitemapUrlsAsync(UrlOrigin.BaseUri(baseUri), ctx, seedRobots);

            var normalizedSeed = UrlNormalizer.Normalize(baseUri);
            ctx.SeedUrl = normalizedSeed;
            if (ctx.Visited.Add(normalizedSeed))
            {
                if (CrawlPolicy.IsAllowedByRobots(normalizedSeed, seedRobots))
                {
                    ctx.Queue.Enqueue(normalizedSeed);
                }
                else
                {
                    ctx.Observer.OnSeedDisallowed(normalizedSeed);
                }
            }

            (producedJobs, indexedCount) = await producer.ProduceAsync(maxPages, maxPagesPerHost);

            completedNaturally = ctx.Queue.Count == 0
                && !ctx.HostCapSkipped
                && producedJobs > 0;
        }

        // The crawler is done fetching; park its lane so the watchdog doesn't read its last fetch as a
        // stall while the embedder drains its backlog (which can outlast the crawl itself many times over).
        heartbeat.MarkCrawler(CrawlHeartbeat.Idle);

        try
        {
            embeddingChannel.Writer.Complete();
            await embedderTask;

            // NOTE: Both halves have now drained, so the orchestrator is the only writer from here — the
            // post-crawl passes touch crawl state and chunks without contention and so need no gate.
            ctx.Observer.OnPhaseChanged(CrawlPhase.RemovingBanned);
            ctx.Observer.OnBannedUrlsRemoved(await robotsService.RemoveRobotsBannedUrlsAsync(ctx));
            if (completedNaturally)
            {
                ctx.Observer.OnPhaseChanged(CrawlPhase.Pruning);
                ctx.Observer.OnStaleUrlsPruned(await producer.PruneStaleUrlsAsync(crawlStartUtc));
            }
            ctx.Observer.OnPhaseChanged(CrawlPhase.Optimizing);
            await CrawlStore.OptimizeDatabaseAsync(ctx.Write, _logger);
        }
        finally
        {
            embeddingChannel.Writer.TryComplete();
            try { await embedderTask; } catch { }
        }

        await linkVerifier.VerifyUndeterminedLinksAsync(ctx, crawlStartUtc);

        // The crawl and all its post-crawl passes are done; stop the watchdog before the final tally.
        watchdogTimer.Dispose();
        await watchdogTask;

        ctx.Observer.OnPhaseChanged(CrawlPhase.Completed);

        var (indexedUrlsInDb, crawlStateRowsInDb) = await CrawlStore.GetCountsAsync(ctx.Read);
        long itemsDeleted = ctx.Observer.Stats.Gone + ctx.Observer.Stats.RemovedBanned + ctx.Observer.Stats.RemovedStale;
        long itemsAdded = Math.Max(0, indexedUrlsInDb - indexedUrlsAtStart + itemsDeleted);

        var (brokenLinks, redirectedLinks) = await linkVerifier.BuildLinkReportAsync(ctx, crawlStartUtc);


        ctx.Observer.OnCrawlCompleted(seedUrl);

        return new CrawlReport(
            string.IsNullOrEmpty(ctx.SeedUrl) ? seedUrl : ctx.SeedUrl,
            crawlStartUtc,
            DateTime.UtcNow,
            completedNaturally,
            ctx.Observer.Stats.Snapshot(CrawlPhase.Completed, ctx.Visited.Count, DateTime.UtcNow - crawlStartUtc),
            indexedUrlsInDb,
            crawlStateRowsInDb,
            itemsAdded,
            itemsDeleted,
            brokenLinks,
            redirectedLinks,
            ctx.HostHealth.UnreachableHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray(),
            backlog.Processed,
            backlog.Queued);
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
    /// Polls the heartbeat on a timer and logs a warning whenever the current activity has been in
    /// flight past <see cref="StallThreshold"/>, naming the URL or phase that is stuck. The loop ends
    /// when <paramref name="timer"/> is disposed.
    /// </summary>
    /// <param name="heartbeat">The shared activity marker the producer and consumer bump.</param>
    /// <param name="timer">The periodic timer driving the poll; disposing it stops the loop.</param>
    /// <returns>A <see cref="Task"/> that completes when the timer is disposed.</returns>
    private async Task RunStallWatchdogAsync(CrawlHeartbeat heartbeat, PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync())
        {
            WarnIfStalled("crawler", heartbeat.ReadCrawler());
            WarnIfStalled("embedder", heartbeat.ReadEmbedder());
        }
    }

    /// <summary>
    /// Logs a possible-stall warning for one heartbeat lane, naming which half of the pipeline is stuck so
    /// the log points at the real culprit instead of whichever thread happened to mark last. An idle lane
    /// (its thread parked with no work) is never a stall, so it is skipped.
    /// </summary>
    /// <param name="lane">The lane label, "crawler" or "embedder".</param>
    /// <param name="state">The lane's current activity and how long it has been running.</param>
    private void WarnIfStalled(string lane, (string Activity, TimeSpan Elapsed) state)
    {
        if (state.Activity != CrawlHeartbeat.Idle && state.Elapsed >= StallThreshold)
        {
            _logger.LogWarning("Possible stall: {Lane} '{Activity}' has been running for {Seconds}s.",
                lane, state.Activity, (int)state.Elapsed.TotalSeconds);
        }
    }
}
