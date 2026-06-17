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
/// Orchestrates a crawl: initializes the shared context, sets up connections, and coordinates
/// a concurrent producer (CrawlProducer) and consumer (CrawlConsumer).
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
    /// <param name="maxPagesPerHost">The maximum pages to crawl on any single host.</param>
    /// <param name="maxCrawlSizeBytes">The maximum size in bytes allowed for a crawled page/file.</param>
    /// <param name="checkExternalLinks">Whether to check external links after the crawl.</param>
    /// <param name="reporter">Receives live progress and phase changes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="CrawlReport"/> summarizing what the crawl indexed, removed, and discovered.</returns>
    public async Task<CrawlReport> CrawlAsync(
        string seedUrl,
        int maxPages = int.MaxValue,
        IEnumerable<string>? allowedServers = null,
        int maxPagesPerHost = int.MaxValue,
        long maxCrawlSizeBytes = 15 * 1024 * 1024,
        bool checkExternalLinks = false,
        ICrawlReporter? reporter = null,
        CancellationToken cancellationToken = default)
    {
        var crawlStartUtc = DateTime.UtcNow;
        reporter ??= NullCrawlReporter.Instance;
        var observer = new CrawlObserver(_logger, reporter, crawlStartUtc);

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

        ctx.Observer.OnPhaseChanged(CrawlPhase.Starting);

        await using var readConnection = new SqliteConnection(_connectionString);
        await readConnection.OpenAsync(cancellationToken);
        await using var writeConnection = new SqliteConnection(_connectionString);
        await writeConnection.OpenAsync(cancellationToken);
        ctx.Read = readConnection;
        ctx.Write = writeConnection;

        var (indexedUrlsAtStart, _) = await CrawlStore.GetCountsAsync(ctx.Read, cancellationToken);

        var channel = Channel.CreateBounded<CrawlJob>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var consumer = new CrawlConsumer(ctx.Write, channel.Reader, _vectorSearchService, _logger);
        var consumerTask = consumer.ConsumeAsync();

        using var consumerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = consumerTask.ContinueWith(t =>
        {
            try
            {
                consumerCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }, TaskContinuationOptions.ExecuteSynchronously);

        var robotsService = new RobotsService(_httpClient, _vectorSearchService, _logger);
        var sitemapService = new SitemapService(_httpClient, _logger);
        var pageDownloader = new PageDownloader(_httpClient);
        var linkVerifier = new LinkVerifier(_httpClient);

        var producer = new CrawlProducer(_vectorSearchService, channel.Writer, ctx, robotsService, pageDownloader, _logger);
        var seedRobots = await robotsService.GetOrFetchRobotsAsync(baseUri, ctx, cancellationToken);

        int producedJobs = 0;
        int indexedCount = 0;
        bool completedNaturally = false;

        if (ctx.HostHealth.IsUnreachable(baseUri.Host))
        {
            ctx.Observer.OnSeedUnreachable(baseUri.Host);
        }
        else
        {
            await sitemapService.EnqueueSitemapUrlsAsync(UrlOrigin.BaseUri(baseUri), ctx, seedRobots, cancellationToken);

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

            // ProduceAsync returns gracefully on cancellation (user or consumer-fault). A consumer
            // fault surfaces below at 'await consumerTask' once the channel is completed.
            (producedJobs, indexedCount) = await producer.ProduceAsync(maxPages, maxPagesPerHost, consumerCts.Token);

            completedNaturally = ctx.Queue.Count == 0
                && !cancellationToken.IsCancellationRequested
                && !ctx.HostCapSkipped
                && producedJobs > 0;
        }

        try
        {
            channel.Writer.Complete();
            await consumerTask;

            // NOTE: The main crawl consumer task has finished, so the orchestrator now drives post-crawl cleanup writes.
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
            channel.Writer.TryComplete();
            try { await consumerTask; } catch { }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await linkVerifier.VerifyUndeterminedLinksAsync(ctx, crawlStartUtc, cancellationToken);
        }

        bool cancelled = cancellationToken.IsCancellationRequested;
        ctx.Observer.OnPhaseChanged(cancelled ? CrawlPhase.Cancelled : CrawlPhase.Completed);

        var (indexedUrlsInDb, crawlStateRowsInDb) = await CrawlStore.GetCountsAsync(ctx.Read, CancellationToken.None);
        long itemsDeleted = ctx.Observer.Stats.Gone + ctx.Observer.Stats.RemovedBanned + ctx.Observer.Stats.RemovedStale;
        long itemsAdded = Math.Max(0, indexedUrlsInDb - indexedUrlsAtStart + itemsDeleted);

        var (brokenLinks, redirectedLinks) = await linkVerifier.BuildLinkReportAsync(ctx, crawlStartUtc);


        ctx.Observer.OnCrawlCompleted(seedUrl);

        return new CrawlReport(
            string.IsNullOrEmpty(ctx.SeedUrl) ? seedUrl : ctx.SeedUrl,
            crawlStartUtc,
            DateTime.UtcNow,
            completedNaturally,
            cancelled,
            ctx.Observer.Stats.Snapshot(cancelled ? CrawlPhase.Cancelled : CrawlPhase.Completed, ctx.Visited.Count, DateTime.UtcNow - crawlStartUtc),
            indexedUrlsInDb,
            crawlStateRowsInDb,
            itemsAdded,
            itemsDeleted,
            brokenLinks,
            redirectedLinks,
            ctx.HostHealth.UnreachableHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>
    /// Creates an empty crawl report in the event of an early failure or invalid input.
    /// </summary>
    /// <param name="seedUrl">The seed URL that failed validation or was unreachable.</param>
    /// <param name="startedUtc">The time the crawl was initiated.</param>
    /// <returns>An empty <see cref="CrawlReport"/>.</returns>
    private static CrawlReport EmptyReport(string seedUrl, DateTime startedUtc) => new(
        seedUrl, startedUtc, DateTime.UtcNow, false, false,
        new CrawlStats().Snapshot(CrawlPhase.Completed, 0, TimeSpan.Zero), 0, 0, 0, 0,
        Array.Empty<BrokenLink>(), Array.Empty<BrokenLink>(), Array.Empty<string>());
}
