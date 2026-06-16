using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.Crawling.Extraction;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Orchestrates a crawl: a single producer fetches and parses pages, handing units of work to a single indexer.
/// </summary>
public partial class CrawlerService
{
    /// <summary>Identifies this crawler in request headers and for robots.txt matching.</summary>
    public const string UserAgent = "LocalSearchEngine-Bot/1.0";

    /// <summary>Lowercased token used to match our own robots rules.</summary>
    private const string UserAgentToken = "localsearchengine-bot";

    /// <summary>Minimum politeness gap between requests to the same host.</summary>
    private const int DefaultRequestDelayMs = 250;

    /// <summary>Upper bound honored for a robots.txt Crawl-delay; larger values are clamped.</summary>
    private const int MaxCrawlDelaySeconds = 30;

    /// <summary>Maximum hosts probed concurrently during end-of-crawl link verification.</summary>
    private const int LinkCheckConcurrency = 8;

    /// <summary>Politeness gap between consecutive probes to the same host during link verification.</summary>
    private static readonly TimeSpan LinkCheckPerHostGap = TimeSpan.FromMilliseconds(250);

    static CrawlerService()
    {
        // Register legacy code-page encodings (windows-1252, etc.) so a sitemap's XML declaration
        // can resolve them during EnqueueSitemapUrlsAsync, which runs before the first HTML page is
        // fetched — and thus before ContentExtractor's own static initializer would register them.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly HttpClient _httpClient;
    private readonly VectorSearchService _vectorSearchService;
    private readonly ILogger<CrawlerService> _logger;
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
    /// Orchestrates the crawl loop starting from a seed URL. A crawl that drains its frontier
    /// completely (no cancellation and no page cap cutting it short) finishes by pruning index
    /// entries for in-scope URLs it could no longer reach; see <see cref="PruneStaleUrlsAsync"/>.
    /// </summary>
    /// <param name="seedUrl">The starting URL of the crawl. Its exact origin (scheme, host, and port) is always in scope.</param>
    /// <param name="maxPages">The maximum number of pages to index.</param>
    /// <param name="allowedServers">Optional additional allowed hosts, each of the form <c>[scheme://]host[:port]</c>; an omitted scheme or port matches any.</param>
    /// <param name="maxPagesPerHost">The maximum pages to crawl on any single host.</param>
    /// <param name="maxCrawlSizeBytes">The maximum size in bytes allowed for a crawled page/file.</param>
    /// <param name="reporter">Receives live progress and phase changes; defaults to a no-op reporter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="CrawlReport"/> summarizing what the crawl indexed, removed, and discovered.</returns>
    public async Task<CrawlReport> CrawlAsync(string seedUrl, int maxPages = int.MaxValue, IEnumerable<string>? allowedServers = null, int maxPagesPerHost = int.MaxValue, long maxCrawlSizeBytes = 15 * 1024 * 1024, bool checkExternalLinks = false, ICrawlReporter? reporter = null, CancellationToken cancellationToken = default)
    {
        // Stamped before anything is visited: after a crawl that drains naturally, any in-scope
        // row whose LastCrawled predates this moment was unreachable this run and gets pruned. It
        // also anchors the elapsed-time figure the reporter stamps onto each snapshot.
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
        // The seed pins its exact origin: an http seed without an explicit port means http on
        // port 80 only. Note the seed's "www." variant is NOT implied — pass it as an
        // allowed-server entry to crawl both.
        ctx.AllowedHosts.AddOrigin(baseUri);

        ctx.Observer.OnPhaseChanged(CrawlPhase.Starting);

        // One read connection for the producer and one write connection for the indexer, both held
        // open for the whole crawl rather than reopened per database call. They run on separate
        // tasks, so two connections (not one shared) keep SQLite's single-writer rule intact while
        // the producer's reads overlap the indexer's writes.
        await using var readConnection = new SqliteConnection(_connectionString);
        await readConnection.OpenAsync(cancellationToken);
        await using var writeConnection = new SqliteConnection(_connectionString);
        await writeConnection.OpenAsync(cancellationToken);
        ctx.Read = readConnection;
        ctx.Write = writeConnection;

        // The indexed-URL count before this run, so the final report can express "items added" as a
        // net change rather than re-counting pages that were already in the database.
        var (indexedUrlsAtStart, _) = await CrawlStore.GetCountsAsync(ctx.Read, cancellationToken);

        // Robots for the seed's origin are needed right away (the seed is enqueued below). Every
        // other origin gets its robots fetched lazily on first contact — being listed in the
        // allowed hosts never by itself causes requests to a server.
        var seedRobots = await GetOrFetchRobotsAsync(ctx, baseUri, cancellationToken);

        if (ctx.HostHealth.IsUnreachable(baseUri.Host))
        {
            // The seed's own server didn't answer its very first request (robots.txt). There is
            // nothing to crawl: skip sitemap discovery and don't enqueue the seed. The empty frontier
            // means the run produces no jobs and so prunes nothing.
            ctx.Observer.OnSeedUnreachable(baseUri.Host);
        }
        else
        {
            // Seed the frontier from the origin's sitemaps; the seed URL itself is enqueued below.
            await EnqueueSitemapUrlsAsync(UrlOrigin.BaseUri(baseUri), ctx, cancellationToken);

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
        }

        int indexedCount = 0;
        int producedJobs = 0;
        bool completedNaturally = false;
        // The producer (this loop) fetches and parses pages, owning the queue/visited set and
        // doing only database READS. It hands the resulting work to a single indexer
        // (consumer), which is the sole database writer and the sole caller of the embedder, so
        // embedding + writes for one page overlap the producer's next fetch and politeness wait.
        // One consumer applying its writes sequentially preserves the single-writer invariant.
        var channel = Channel.CreateBounded<CrawlJob>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait, // backpressure when the indexer falls behind
        });
        var indexer = ConsumeAsync(ctx.Write, channel.Reader);

        try
        {
            ctx.Observer.OnPhaseChanged(CrawlPhase.Crawling);
            while (ctx.Queue.Count > 0 && indexedCount < maxPages)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ctx.Observer.OnCrawlCancelled(indexedCount);
                    break;
                }

                var currentUrl = ctx.Queue.Dequeue();

                // Safety valve against crawler traps (calendars, faceted nav): once a host has
                // contributed its cap of indexed pages, stop fetching more of its URLs.
                if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentHostUri)
                    && ctx.IndexedPerHost.TryGetValue(currentHostUri.Host, out var hostIndexed)
                    && hostIndexed >= maxPagesPerHost)
                {
                    // A skipped URL means "not visited" no longer implies "gone", so this run
                    // must not prune.
                    ctx.HostCapSkipped = true;
                    ctx.Observer.OnHostCapReached(maxPagesPerHost, currentHostUri.Host, currentUrl);
                    continue;
                }

                // A host written off as unreachable earlier this run: don't spend any more requests
                // on it. Its URLs are exempt from stale pruning (see PruneStaleUrlsAsync), so skipping
                // them here never drops their existing index entries.
                if (currentHostUri is not null && ctx.HostHealth.IsUnreachable(currentHostUri.Host))
                {
                    continue;
                }

                ctx.Observer.OnPageFetching(indexedCount, ctx.Visited.Count, currentUrl);

                CrawlJob? job;
                try
                {
                    job = await ProduceJobAsync(ctx, currentUrl, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ctx.Observer.OnFetchCancelled(currentUrl);
                    break;
                }
                catch (Exception ex)
                {
                    // Fetch/parse failed unexpectedly: note the visit but KEEP any content
                    // already indexed for this URL — a transient failure must not erase data.
                    ctx.Observer.OnFetchError(ex, currentUrl);
                    job = new TouchJob(currentUrl, 500);
                }

                if (job is not null)
                {
                    producedJobs++;
                    await channel.Writer.WriteAsync(job, CancellationToken.None);
                    if (job is IndexJob)
                    {
                        indexedCount++;
                        if (Uri.TryCreate(job.Url, UriKind.Absolute, out var indexedUri))
                        {
                            ctx.IndexedPerHost.TryGetValue(indexedUri.Host, out var n);
                            ctx.IndexedPerHost[indexedUri.Host] = n + 1;
                        }
                    }
                }
            }

            // Pruning trusts a crawl only when the frontier drained on its own: not cancelled,
            // not cut short by the page or per-host caps, and at least one URL actually
            // contacted (a crawl that produced nothing — say robots.txt failed over to
            // disallow-all — proves nothing about what still exists).
            completedNaturally = ctx.Queue.Count == 0
                && !cancellationToken.IsCancellationRequested
                && !ctx.HostCapSkipped
                && producedJobs > 0;
        }
        finally
        {
            // Stop the indexer, let it drain everything already fetched, then tidy the database.
            channel.Writer.Complete();
            await indexer;
            // A robots Disallow is a definite signal, so already-indexed URLs an origin's robots.txt
            // now bans are dropped after every crawl — capped or cancelled included. Stale-URL
            // pruning, by contrast, stays gated on a natural completion, where "unreached" can be
            // trusted to mean "gone".
            ctx.Observer.OnPhaseChanged(CrawlPhase.RemovingBanned);
            ctx.Observer.OnBannedUrlsRemoved(await RemoveRobotsBannedUrlsAsync(ctx));
            if (completedNaturally)
            {
                ctx.Observer.OnPhaseChanged(CrawlPhase.Pruning);
                ctx.Observer.OnStaleUrlsPruned(await PruneStaleUrlsAsync(ctx, crawlStartUtc));
            }
            ctx.Observer.OnPhaseChanged(CrawlPhase.Optimizing);
            await CrawlStore.OptimizeDatabaseAsync(ctx.Write, _logger);
        }

        // Verify links not already determined this run — off-site links (never crawled) plus any
        // in-scope links the crawl didn't reach — with a single lazy probe each. This is a final
        // reporting pass, not a crawl, and is skipped entirely on cancellation.
        if (!cancellationToken.IsCancellationRequested)
        {
            await VerifyUndeterminedLinksAsync(ctx, crawlStartUtc, cancellationToken);
        }

        bool cancelled = cancellationToken.IsCancellationRequested;
        ctx.Observer.OnPhaseChanged(cancelled ? CrawlPhase.Cancelled : CrawlPhase.Completed);

        var (indexedUrlsInDb, crawlStateRowsInDb) = await CrawlStore.GetCountsAsync(ctx.Read, CancellationToken.None);
        long itemsDeleted = ctx.Observer.Stats.Gone + ctx.Observer.Stats.RemovedBanned + ctx.Observer.Stats.RemovedStale;
        // end = start + added - deleted, so added = end - start + deleted. Clamped at zero against
        // a concurrent writer skewing the before/after counts.
        long itemsAdded = Math.Max(0, indexedUrlsInDb - indexedUrlsAtStart + itemsDeleted);

        // The broken/redirected link lists come from the link index (the source of truth), scoped to
        // links found on this crawl's pages and determined this run.
        var (brokenLinks, redirectedLinks) = await BuildLinkReportAsync(ctx, crawlStartUtc);

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

    /// <summary>Builds the report returned when a crawl cannot start (e.g. an invalid seed URL).</summary>
    /// <param name="seedUrl">The seed URL that was rejected.</param>
    /// <param name="startedUtc">When the crawl was attempted.</param>
    /// <returns>An all-zero <see cref="CrawlReport"/>.</returns>
    private static CrawlReport EmptyReport(string seedUrl, DateTime startedUtc) => new(
        seedUrl, startedUtc, DateTime.UtcNow, false, false,
        new CrawlStats().Snapshot(CrawlPhase.Completed, 0, TimeSpan.Zero), 0, 0, 0, 0,
        Array.Empty<BrokenLink>(), Array.Empty<BrokenLink>(), Array.Empty<string>());



    /// <summary>
    /// Fetches and analyzes a single URL, resolving page redirections, content types, hashes, and outlinks.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="currentUrl">The URL to process.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="CrawlJob"/> representing the classification of work for the database writer, or <c>null</c> if skipped.</returns>
    private async Task<CrawlJob?> ProduceJobAsync(CrawlContext ctx, string currentUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri)) return null;
        if (!ctx.AllowedHosts.IsAllowed(currentUri))
        {
            ctx.Observer.OnOutScopeUrlReached(currentUrl);
            return null;
        }

        // Frontier filters allow optimistically when an origin's robots aren't cached yet, so
        // the authoritative robots check happens here — after a lazy per-origin robots fetch
        // and before any politeness wait or page request is spent on a disallowed URL.
        var currentRobots = await GetOrFetchRobotsAsync(ctx, currentUri, cancellationToken);

        // Fetching robots.txt is our first contact with a host. If that connection failed, the host
        // has just been written off — don't spend a page request on it. Its other URLs are skipped
        // back in the crawl loop.
        if (ctx.HostHealth.IsUnreachable(currentUri.Host))
        {
            return null;
        }

        if (!CrawlPolicy.IsAllowedByRobots(currentUrl, currentRobots))
        {
            ctx.Observer.OnPageDisallowed(currentUrl);
            return null;
        }
        await DelayForHostAsync(ctx, currentUri.Host, ResolveRequestDelay(currentRobots), cancellationToken);

        var state = await CrawlStore.GetCrawlStateAsync(ctx.Read, currentUrl, cancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
        if (!string.IsNullOrEmpty(state.ETag))
        {
            request.Headers.IfNoneMatch.ParseAdd(state.ETag);
        }
        if (!string.IsNullOrEmpty(state.LastModified) && DateTimeOffset.TryParse(state.LastModified, out var lastModDate))
        {
            request.Headers.IfModifiedSince = lastModDate;
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // The server answered (any status counts), so it can never be written off as unreachable
        // this run: a later connection blip falls back to the normal retry/keep-index handling.
        ctx.HostHealth.RecordContacted(currentUri.Host);

        int statusCode = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            // Unchanged: re-derive the frontier from this page's stored outlinks so the crawl
            // can still reach pages only linked from here.
            ctx.Observer.OnPageUnchanged(currentUrl);
            await EnqueueStoredOutlinksAsync(ctx, currentUrl, cancellationToken);
            return new TouchJob(currentUrl, statusCode);
        }

        // After redirects, the final URL must stay in scope and be robots-allowed before we
        // index its content under that URL.
        var finalUrl = currentUrl;
        string? redirectSourceUrl = null;
        var finalRequestUri = response.RequestMessage?.RequestUri;
        if (finalRequestUri != null)
        {
            var normalizedFinal = UrlNormalizer.Normalize(finalRequestUri);
            if (!string.Equals(normalizedFinal, currentUrl, StringComparison.OrdinalIgnoreCase))
            {
                redirectSourceUrl = currentUrl;

                // In every out-of-scope/duplicate redirect case below we still record a visit for
                // the source URL (rather than returning null), so its CrawlState row reflects how
                // the URL last responded.
                if (!ctx.AllowedHosts.IsAllowed(finalRequestUri))
                {
                    // The seed itself redirecting to a new origin (a vanity domain, or a redirect
                    // landing on a host we didn't anticipate) means the site really lives there:
                    // adopt that origin into scope instead of ending the crawl at the front door.
                    // Out-of-scope redirects from any *other* page stay rejected.
                    if (string.Equals(currentUrl, ctx.SeedUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Observer.OnSeedRedirectedToNewOrigin(currentUrl, UrlOrigin.Key(finalRequestUri));
                        ctx.AllowedHosts.AddOrigin(finalRequestUri);
                    }
                    else
                    {
                        ctx.Observer.OnPageRedirectedOutScope(currentUrl, normalizedFinal);
                        return new AliasJob(currentUrl, 302);
                    }
                }
                var finalRobots = await GetOrFetchRobotsAsync(ctx, finalRequestUri, cancellationToken);
                if (!CrawlPolicy.IsAllowedByRobots(normalizedFinal, finalRobots))
                {
                    ctx.Observer.OnPageRedirectedDisallowed(currentUrl, normalizedFinal);
                    return new AliasJob(currentUrl, 302);
                }
                if (!ctx.Visited.Add(normalizedFinal))
                {
                    ctx.Observer.OnPageRedirectedAlreadySeen(currentUrl, normalizedFinal);
                    return new AliasJob(currentUrl, 302);
                }
                finalUrl = normalizedFinal;
            }
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Genuinely gone: drop it from the index, and record it as a broken link against the
            // page it was first seen on for the broken-links report.
            ctx.Observer.OnPageGone(currentUrl, finalUrl, statusCode);
            return new GoneJob(finalUrl, statusCode, redirectSourceUrl);
        }

        if (!response.IsSuccessStatusCode)
        {
            // 5xx / 403 / etc.: keep whatever is already indexed; just record the visit.
            ctx.Observer.OnPageFailed(currentUrl, finalUrl, statusCode);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        // Check Content-Length first
        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > ctx.MaxCrawlSizeBytes)
        {
            ctx.Observer.OnPageSkippedSize(currentUrl, finalUrl, contentLength.Value, ctx.MaxCrawlSizeBytes);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        // Check Content-Type first
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!CrawlPolicy.IsSupportedOrGenericContentType(contentType))
        {
            ctx.Observer.OnPageSkippedType(currentUrl, finalUrl, contentType);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        // Read stream incrementally with limit and prefix sniffing
        byte[] body;
        using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        using (var bodyStream = new MemoryStream())
        {
            byte[] buffer = new byte[8192];
            int bytesRead;
            bool checkedPrefix = false;

            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                if (bodyStream.Length + bytesRead > ctx.MaxCrawlSizeBytes)
                {
                    ctx.Observer.OnPageSkippedSize(currentUrl, finalUrl, bodyStream.Length + bytesRead, ctx.MaxCrawlSizeBytes);
                    return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
                }

                bodyStream.Write(buffer, 0, bytesRead);

                if (!checkedPrefix && bodyStream.Length >= 4096)
                {
                    checkedPrefix = true;
                    var prefix = bodyStream.ToArray();
                    if (!CrawlPolicy.IsSupportedPrefix(prefix, contentType, finalUrl))
                    {
                        ctx.Observer.OnPageSkippedType(currentUrl, finalUrl, contentType);
                        return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
                    }
                }
            }

            body = bodyStream.ToArray();

            // Final fallback check if stream ended before 4KB and was never checked
            if (!checkedPrefix)
            {
                if (!CrawlPolicy.IsSupportedPrefix(body, contentType, finalUrl))
                {
                    ctx.Observer.OnPageSkippedType(currentUrl, finalUrl, contentType);
                    return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
                }
            }
        }

        // Byte-identical to the last successful crawl? Treat exactly like a 304 — re-enqueue
        // stored outlinks, skip the expensive re-embed. The chunk check makes this self-healing:
        // if a crash left the stored hash but wiped the chunks (or a noindex was lifted without
        // the bytes changing), we fall through and re-index instead of hiding the page forever.
        string newHash = Convert.ToHexString(SHA256.HashData(body));
        var finalState = string.Equals(finalUrl, currentUrl, StringComparison.OrdinalIgnoreCase)
            ? state
            : await CrawlStore.GetCrawlStateAsync(ctx.Read, finalUrl, cancellationToken);

        if (finalState.ContentHash is not null && finalState.ContentHash == newHash
            && await CrawlStore.UrlHasChunksAsync(ctx.Read, finalUrl, cancellationToken))
        {
            ctx.Observer.OnPageUnchangedHash(currentUrl, finalUrl);
            await EnqueueStoredOutlinksAsync(ctx, finalUrl, cancellationToken);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        // A different URL already serves byte-identical, indexed content (e.g. www vs non-www,
        // or /page vs /page/index.html). Alias to it instead of indexing a second copy. No
        // ContentHash is stored for an alias, so it's re-evaluated each crawl and self-heals
        // if the canonical later diverges or disappears.
        //
        // Check the in-run map first: the indexer is asynchronous, so a copy we decided to index
        // moments ago may not have its chunks written yet, and the DB lookup alone would miss it.
        string? duplicateOf = null;
        if (ctx.IndexedContentHashes.TryGetValue(newHash, out var inRunUrl)
            && !string.Equals(inRunUrl, finalUrl, StringComparison.OrdinalIgnoreCase))
        {
            duplicateOf = inRunUrl;
        }
        duplicateOf ??= await CrawlStore.FindIndexedDuplicateAsync(ctx.Read, newHash, finalUrl, cancellationToken);
        if (duplicateOf != null)
        {
            ctx.Observer.OnPageDuplicateContent(currentUrl, finalUrl, duplicateOf);
            EnqueueSingle(ctx, duplicateOf);
            return new AliasJob(finalUrl, statusCode, redirectSourceUrl);
        }

        string? newETag = response.Headers.ETag?.Tag;
        string? newLastModified = response.Content.Headers.LastModified?.ToString("r");

        // How to parse the body is decided by the server-declared Content-Type (falling back to
        // byte sniffing), never the URL's file extension — see CrawlPolicy.ClassifyContent.
        var kind = CrawlPolicy.ClassifyContent(contentType, body);

        if (kind == DocKind.Pdf)
        {
            var (pdfTitle, pdfText) = ContentExtractor.ExtractPdf(body);
            // Surface the embedded document title as both the stored title and a heading chunk,
            // mirroring how an HTML <title> is indexed, so documents aren't bare URLs in results.
            ctx.IndexedContentHashes[newHash] = finalUrl;
            ctx.Observer.OnPageIndexed(currentUrl, finalUrl, 0);
            return new IndexJob(finalUrl, statusCode, pdfTitle, pdfTitle ?? string.Empty, pdfText,
                newETag, newLastModified, newHash, Array.Empty<string>(), Array.Empty<string>(), redirectSourceUrl);
        }

        if (kind == DocKind.Docx)
        {
            var (docxTitle, docxText) = ContentExtractor.ExtractDocx(body);
            ctx.IndexedContentHashes[newHash] = finalUrl;
            ctx.Observer.OnPageIndexed(currentUrl, finalUrl, 0);
            return new IndexJob(finalUrl, statusCode, docxTitle, docxTitle ?? string.Empty, docxText,
                newETag, newLastModified, newHash, Array.Empty<string>(), Array.Empty<string>(), redirectSourceUrl);
        }

        // Not a document we extract and not HTML (by Content-Type or sniff): skip it, so JSON,
        // images, etc. served at extensionless or dynamic routes don't get parsed as a web page.
        if (kind != DocKind.Html)
        {
            ctx.Observer.OnPageSkippedType(currentUrl, finalUrl, contentType);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        var xRobotsTag = response.Headers.TryGetValues("X-Robots-Tag", out var values)
            ? string.Join(",", values)
            : null;
        var analysis = ContentExtractor.AnalyzeHtml(body, response.Content.Headers.ContentType?.CharSet, xRobotsTag, finalUrl,
            ctx.AllowedHosts, ctx.RobotsCache, UserAgentToken);

        // A canonical link pointing elsewhere marks this URL as an alias: crawl the canonical
        // copy instead and don't index a duplicate here.
        if (analysis.CanonicalAlias != null)
        {
            ctx.Observer.OnPageAlias(currentUrl, finalUrl, analysis.CanonicalAlias);
            EnqueueSingle(ctx, analysis.CanonicalAlias);
            return new AliasJob(finalUrl, statusCode, redirectSourceUrl);
        }

        // Enqueue newly discovered in-scope links now (producer-owned frontier); the indexer
        // persists this page's full link set — in-scope and off-site — to the link index for future
        // 304/unchanged re-crawls and end-of-crawl verification.
        ctx.Observer.OnOutlinksAdded(analysis.Outlinks.Count);
        foreach (var link in analysis.Outlinks)
        {
            Discover(ctx, link);
        }

        if (analysis.NoIndex)
        {
            // Respect noindex: ensure it isn't in the index, but keep crawl state + links.
            ctx.Observer.OnPageNoIndex(currentUrl, finalUrl);
            return new NoIndexJob(finalUrl, statusCode, analysis.Title, newETag, newLastModified, newHash, analysis.Outlinks, analysis.OffsiteLinks, redirectSourceUrl);
        }

        ctx.IndexedContentHashes[newHash] = finalUrl;
        ctx.Observer.OnPageIndexed(currentUrl, finalUrl, analysis.Outlinks.Count);
        return new IndexJob(finalUrl, statusCode, analysis.Title, analysis.Headings, analysis.Text,
            newETag, newLastModified, newHash, analysis.Outlinks, analysis.OffsiteLinks, redirectSourceUrl);
    }

    /// <summary>
    /// Reads crawl jobs from the channel and persists indexing changes and visit states to the database.
    /// </summary>
    /// <param name="connection">The open write database connection.</param>
    /// <param name="reader">The channel reader producing crawl jobs.</param>
    /// <returns>A <see cref="Task"/> representing the database writing process.</returns>
    private async Task ConsumeAsync(SqliteConnection connection, ChannelReader<CrawlJob> reader)
    {
        await foreach (var job in reader.ReadAllAsync())
        {
            try
            {
                if (job.RedirectSourceUrl != null)
                {
                    await _vectorSearchService.DeleteUrlChunksAsync(job.RedirectSourceUrl);
                    await CrawlStore.DeleteLinksAsync(connection, job.RedirectSourceUrl, CancellationToken.None);
                    await CrawlStore.RecordVisitAsync(connection, job.RedirectSourceUrl, 302, clearMetadata: true, CancellationToken.None);
                }

                switch (job)
                {
                    case IndexJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await _vectorSearchService.IndexUrlChunksAsync(j.Url, j.Text, isHeading: false);
                        if (!string.IsNullOrWhiteSpace(j.Headings))
                        {
                            await _vectorSearchService.IndexUrlChunksAsync(j.Url, j.Headings, isHeading: true);
                        }
                        await CrawlStore.RecordCrawlStateAsync(connection, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, CancellationToken.None);
                        await CrawlStore.StoreLinksAsync(connection, j.Url, j.Outlinks, j.OffsiteLinks, CancellationToken.None);

                        break;

                    case NoIndexJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await CrawlStore.StoreLinksAsync(connection, j.Url, j.Outlinks, j.OffsiteLinks, CancellationToken.None);
                        await CrawlStore.RecordCrawlStateAsync(connection, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, CancellationToken.None);
                        break;

                    case GoneJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await CrawlStore.DeleteLinksAsync(connection, j.Url, CancellationToken.None);
                        await CrawlStore.RecordVisitAsync(connection, j.Url, j.StatusCode, clearMetadata: true, CancellationToken.None);
                        break;

                    case AliasJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await CrawlStore.DeleteLinksAsync(connection, j.Url, CancellationToken.None);
                        await CrawlStore.RecordVisitAsync(connection, j.Url, j.StatusCode, clearMetadata: true, CancellationToken.None);
                        break;

                    case TouchJob j:
                        await CrawlStore.RecordVisitAsync(connection, j.Url, j.StatusCode, clearMetadata: false, CancellationToken.None);
                        break;
                }

                // Record how this destination last responded against every link in the index that
                // points at it — so links to it (including from pages not re-parsed this run) reflect
                // its current status. The dequeued URL is the redirect source when a redirect was
                // followed, otherwise the job's own URL.
                var dequeuedUrl = job.RedirectSourceUrl ?? job.Url;
                await CrawlStore.UpdateLinkStatusByDestinationAsync(connection, dequeuedUrl, (int)ClassifyLinkStatus(job), job.StatusCode, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply crawl result for {Url}", job.Url);
            }
        }
    }

    /// <summary>
    /// Represents the base crawl job result class containing URL and HTTP status information.
    /// </summary>
    /// <param name="Url">The target page URL.</param>
    /// <param name="StatusCode">The response HTTP status code.</param>
    /// <param name="RedirectSourceUrl">The source URL if this job was reached via redirect.</param>
    private abstract record CrawlJob(string Url, int StatusCode, string? RedirectSourceUrl = null);

    /// <summary>
    /// Represents a job classification to fully index the page content and outlinks.
    /// </summary>
    private sealed record IndexJob(
        string Url, int StatusCode, string? Title, string Headings, string Text,
        string? ETag, string? LastModified, string ContentHash, IReadOnlyCollection<string> Outlinks,
        IReadOnlyCollection<string> OffsiteLinks, string? RedirectSourceUrl = null)
        : CrawlJob(Url, StatusCode, RedirectSourceUrl);

    /// <summary>
    /// Represents a job classification where indexing is skipped but crawl state and outlinks are stored.
    /// </summary>
    private sealed record NoIndexJob(
        string Url, int StatusCode, string? Title, string? ETag, string? LastModified,
        string ContentHash, IReadOnlyCollection<string> Outlinks, IReadOnlyCollection<string> OffsiteLinks,
        string? RedirectSourceUrl = null)
        : CrawlJob(Url, StatusCode, RedirectSourceUrl);

    /// <summary>
    /// Represents a job classification for pages that returned 404 or 410 Gone status.
    /// </summary>
    private sealed record GoneJob(string Url, int StatusCode, string? RedirectSourceUrl = null)
        : CrawlJob(Url, StatusCode, RedirectSourceUrl);

    /// <summary>
    /// Represents a job classification for canonical page aliases.
    /// </summary>
    private sealed record AliasJob(string Url, int StatusCode, string? RedirectSourceUrl = null)
        : CrawlJob(Url, StatusCode, RedirectSourceUrl);

    /// <summary>
    /// Represents a job classification for unchanged pages (304) or transient errors.
    /// </summary>
    private sealed record TouchJob(string Url, int StatusCode, string? RedirectSourceUrl = null)
        : CrawlJob(Url, StatusCode, RedirectSourceUrl);

    /// <summary>
    /// Removes index entries for in-scope URLs the completed crawl never reached. Runs only
    /// after a crawl that drained its frontier naturally, where "not visited this run" reliably
    /// means "no longer reachable": orphaned pages whose links were removed, and pages a robots
    /// rule now disallows. Rows on hosts outside this crawl's scope (e.g. another site sharing
    /// the database) are never touched, and origins whose robots.txt was unavailable (5xx) this
    /// run are exempt — their URLs went unvisited for reasons that say nothing about staleness.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="crawlStartUtc">The crawl's start time; rows last visited before it are stale.</param>
    /// <returns>The number of stale URLs pruned.</returns>
    private async Task<int> PruneStaleUrlsAsync(CrawlContext ctx, DateTime crawlStartUtc)
    {
        int pruned = 0;
        try
        {
            var candidates = await CrawlStore.GetUrlsNotCrawledSinceAsync(ctx.Read, crawlStartUtc, CancellationToken.None);
            foreach (var url in candidates)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                if (!ctx.AllowedHosts.IsAllowed(uri)) continue;
                if (ctx.RobotsUnavailable.Contains(UrlOrigin.Key(uri))) continue;
                // A host written off as unreachable this run went unvisited for a reason unrelated to
                // staleness — leave its existing index entries alone, exactly like a 5xx robots.txt.
                if (ctx.HostHealth.IsUnreachable(uri.Host)) continue;

                await _vectorSearchService.DeleteUrlChunksAsync(url);
                await CrawlStore.DeleteLinksAsync(ctx.Write, url, CancellationToken.None);
                await CrawlStore.DeleteCrawlStateAsync(ctx.Write, url, CancellationToken.None);
                pruned++;
            }

        }
        catch (Exception ex)
        {
            // Pruning is housekeeping; a failure here must not turn a finished crawl into an error.
            ctx.Observer.OnPruneFailed(ex);
        }
        return pruned;
    }

    /// <summary>
    /// Removes already-indexed URLs that the robots.txt fetched for their origin this crawl now
    /// disallows. Unlike stale-URL pruning this runs after every crawl — capped or cancelled
    /// included — because a robots Disallow is a definite signal, not the "we never reached it"
    /// inference pruning depends on. Only origins actually contacted this run are considered (those
    /// in the robots cache), and an origin whose robots.txt was unavailable (5xx) is skipped: that
    /// stands in as disallow-all, and a transient failure must not wipe the origin's index.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <returns>The number of indexed URLs removed for being newly robots-disallowed.</returns>
    private async Task<int> RemoveRobotsBannedUrlsAsync(CrawlContext ctx)
    {
        int removed = 0;
        try
        {
            foreach (var (origin, rules) in ctx.RobotsCache)
            {
                if (ctx.RobotsUnavailable.Contains(origin)) continue;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) continue;
                // An unreachable host's cached rules are an AllowAll placeholder, not anything it
                // actually served; never drop its indexed URLs on the strength of them.
                if (ctx.HostHealth.IsUnreachable(originUri.Host)) continue;

                // Narrow to the origin's rows in SQL, then confirm each in memory: the prefix is a
                // coarse filter (it also matches a different port, or a look-alike host such as
                // example.com.evil.com), so the exact origin key is the authoritative gate.
                var candidates = await CrawlStore.GetCrawledUrlsWithPrefixAsync(
                    ctx.Read, originUri.GetLeftPart(UriPartial.Authority), CancellationToken.None);
                foreach (var url in candidates)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                    if (!string.Equals(UrlOrigin.Key(uri), origin, StringComparison.OrdinalIgnoreCase)) continue;
                    if (CrawlPolicy.IsAllowedByRobots(url, rules)) continue;

                    await _vectorSearchService.DeleteUrlChunksAsync(url);
                    await CrawlStore.DeleteLinksAsync(ctx.Write, url, CancellationToken.None);
                    await CrawlStore.DeleteCrawlStateAsync(ctx.Write, url, CancellationToken.None);
                    removed++;
                }
            }

        }
        catch (Exception ex)
        {
            // Housekeeping: a failure here must not turn a finished crawl into an error.
            ctx.Observer.OnRemoveBannedFailed(ex);
        }
        return removed;
    }

    /// <summary>
    /// Adds a freshly discovered URL to the frontier if it has not been seen yet.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="url">The URL to enqueue.</param>
    /// <returns><c>true</c> if the URL was newly enqueued; <c>false</c> if it had already been seen.</returns>
    private static bool Discover(CrawlContext ctx, string url)
    {
        if (!ctx.Visited.Add(url)) return false;
        ctx.Queue.Enqueue(url);
        return true;
    }



    /// <summary>
    /// Validates and enqueues a single URL into the frontier queue.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="url">The URL string to enqueue.</param>
    private void EnqueueSingle(CrawlContext ctx, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (!ctx.AllowedHosts.IsAllowed(uri)) return;
        var robots = ctx.RobotsCache.TryGetValue(UrlOrigin.Key(uri), out var r) ? r : RobotsRules.AllowAll;
        if (!CrawlPolicy.IsAllowedByRobots(url, robots)) return;
        Discover(ctx, url);
    }

    /// <summary>
    /// Enqueues outlinks previously discovered and saved for the specified URL.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="url">The URL whose saved outlinks should be enqueued.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task EnqueueStoredOutlinksAsync(CrawlContext ctx, string url, CancellationToken cancellationToken)
    {
        foreach (var link in await CrawlStore.GetStoredOutlinksAsync(ctx.Read, url, cancellationToken))
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) continue;
            if (!ctx.AllowedHosts.IsAllowed(uri)) continue;
            var robots = ctx.RobotsCache.TryGetValue(UrlOrigin.Key(uri), out var r) ? r : RobotsRules.AllowAll;
            if (!CrawlPolicy.IsAllowedByRobots(link, robots)) continue;
            Discover(ctx, link);
        }
    }

    /// <summary>
    /// Implements polite pacing delays between consecutive requests to the same hostname.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="host">The target hostname.</param>
    /// <param name="minGap">The minimum gap duration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the delay wait.</returns>
    private async Task DelayForHostAsync(CrawlContext ctx, string host, TimeSpan minGap, CancellationToken cancellationToken)
    {
        // Track the last fetch per host so multi-host crawls don't serialize on a single
        // global delay: a host we haven't touched recently is ready immediately.
        if (ctx.LastFetchUtc.TryGetValue(host, out var last))
        {
            var wait = minGap - (DateTime.UtcNow - last);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
        }
        ctx.LastFetchUtc[host] = DateTime.UtcNow;
    }


    /// <summary>
    /// Resolves the request delay gap duration configured in robots.txt rules.
    /// </summary>
    /// <param name="robots">The robots rules.</param>
    /// <returns>A <see cref="TimeSpan"/> specifying the delay duration.</returns>
    private static TimeSpan ResolveRequestDelay(RobotsRules robots)
    {
        double ms = DefaultRequestDelayMs;
        if (robots.CrawlDelaySeconds is double seconds && seconds > 0)
        {
            // Honor Crawl-delay, but clamp it: a huge value (misconfigured or hostile
            // robots.txt) must not be able to stall the crawl for minutes per page.
            ms = Math.Max(ms, Math.Min(seconds, MaxCrawlDelaySeconds) * 1000);
        }
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// Classifies an exception thrown while fetching a URL as a connection-level failure — a DNS
    /// resolution failure, a refused/reset/unreachable socket, a TLS handshake failure, or a request
    /// timeout — meaning we are unlikely ever to hear back from the server, as opposed to an error
    /// the server actually answered with. Cancellation requested by the crawl itself never counts.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <param name="cancellationToken">The crawl's cancellation token, to tell our own shutdown apart from a server timeout.</param>
    /// <returns><c>true</c> if the failure indicates an unreachable server; otherwise, <c>false</c>.</returns>
    private static bool IsUnreachableError(Exception ex, CancellationToken cancellationToken)
    {
        // Our own shutdown, not the server's silence: a request aborted because the crawl is being
        // cancelled says nothing about whether the host is reachable.
        if (cancellationToken.IsCancellationRequested) return false;

        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError
                                                           or HttpRequestError.ConnectionError
                                                           or HttpRequestError.SecureConnectionError }:
                case SocketException:
                case TimeoutException:
                // HttpClient's own request timeout surfaces as a TaskCanceledException, and we ruled
                // out our own cancellation above — so the server accepted the socket but never replied.
                case TaskCanceledException:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Verifies the links in the index that weren't determined this run — off-site links (never
    /// crawled) and any in-scope links the crawl didn't reach — so the report reflects the current
    /// run. Each distinct destination gets one lazy liveness probe: a single HEAD (falling back to
    /// GET), no robots, no redirect chasing. The result (ok, redirect, or error) is written back to
    /// every link pointing at that destination. Only links found on pages in this crawl's scope are
    /// considered, and off-site links are skipped unless external checking is enabled. Different
    /// hosts are probed concurrently, each host serially and politely; the status write-backs are
    /// applied sequentially afterward (one write connection).
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="crawlStartUtc">The crawl start; links last updated before it are re-verified.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the verification pass.</returns>
    private async Task VerifyUndeterminedLinksAsync(CrawlContext ctx, DateTime crawlStartUtc, CancellationToken cancellationToken)
    {
        var rows = await CrawlStore.GetLinksToVerifyAsync(ctx.Read, crawlStartUtc, CancellationToken.None);

        // Distinct destinations to probe: links found on a page in this crawl's scope, skipping
        // off-site links unless external checking is on, and skipping destinations on a host already
        // written off as unreachable this run (we've stopped spending requests on it).
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fromUrl, toUrl, external) in rows)
        {
            if (external && !ctx.CheckExternalLinks) continue;
            if (!Uri.TryCreate(fromUrl, UriKind.Absolute, out var fromUri) || !ctx.AllowedHosts.IsAllowed(fromUri)) continue;
            if (Uri.TryCreate(toUrl, UriKind.Absolute, out var toUri) && ctx.HostHealth.IsUnreachable(toUri.Host)) continue;
            destinations.Add(toUrl);
        }

        if (destinations.Count == 0) return;

        ctx.Observer.OnPhaseChanged(CrawlPhase.CheckingLinks);

        var byHost = destinations
            .Select(target => (Target: target, Uri: Uri.TryCreate(target, UriKind.Absolute, out var u) ? u : null))
            .Where(x => x.Uri is not null)
            .GroupBy(x => x.Uri!.Host)
            .ToList();

        var results = new ConcurrentBag<(string Target, LinkStatus Status, int StatusCode)>();
        try
        {
            await Parallel.ForEachAsync(
                byHost,
                new ParallelOptions { MaxDegreeOfParallelism = LinkCheckConcurrency, CancellationToken = cancellationToken },
                async (group, token) =>
                {
                    bool first = true;
                    foreach (var (target, _) in group)
                    {
                        if (!first) await Task.Delay(LinkCheckPerHostGap, token);
                        first = false;

                        var (status, statusCode) = await ProbeLinkAsync(ctx, target, token);
                        results.Add((target, status, statusCode));
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-pass: keep whatever was already probed and stop.
        }

        // Write-backs are applied here, sequentially, rather than from inside the parallel probes:
        // the crawl has a single write connection and SQLite allows only one writer.
        int broken = 0, redirected = 0;
        foreach (var (target, status, statusCode) in results)
        {
            await CrawlStore.UpdateLinkStatusByDestinationAsync(ctx.Write, target, (int)status, statusCode, CancellationToken.None);
            if (status == LinkStatus.Error) broken++;
            else if (status == LinkStatus.Redirect) redirected++;
        }

        ctx.Observer.OnLinksVerified(results.Count, broken, redirected);
    }

    /// <summary>
    /// Probes a single URL to classify it as ok, a redirect, or an error, trying HEAD first and
    /// falling back to a header-only GET if the server rejects HEAD. Redirects aren't chased: one
    /// request, then classify. HttpClient transparently follows redirects, so a redirect surfaces as
    /// a final request URI different from the one asked for (the same signal the crawler uses for
    /// page redirects). A destination that ends on a 4xx/5xx is an error regardless of any redirect
    /// along the way; a connection-level failure is an error; any other failure is inconclusive and
    /// given the benefit of the doubt.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="url">The URL to probe (already normalized).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The classified <see cref="LinkStatus"/> and the HTTP status (0 if the request never completed).</returns>
    private async Task<(LinkStatus Status, int StatusCode)> ProbeLinkAsync(CrawlContext ctx, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendProbeAsync(url, cancellationToken);
            int statusCode = (int)response.StatusCode;

            if (statusCode >= 400)
            {
                return (LinkStatus.Error, statusCode);
            }

            var finalUri = response.RequestMessage?.RequestUri;
            bool redirected = finalUri is not null
                && Uri.TryCreate(url, UriKind.Absolute, out var requested)
                && !string.Equals(UrlNormalizer.Normalize(finalUri), UrlNormalizer.Normalize(requested), StringComparison.OrdinalIgnoreCase);
            if (redirected || statusCode is >= 300 and < 400)
            {
                return (LinkStatus.Redirect, statusCode);
            }
            return (LinkStatus.Ok, statusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A connection-level failure means the link is dead; any other error is inconclusive, so
            // give the link the benefit of the doubt rather than cry wolf over a transient hiccup.
            if (IsUnreachableError(ex, cancellationToken))
            {
                return (LinkStatus.Error, 0);
            }
            ctx.Observer.OnLinkProbeInconclusive(ex, url);
            return (LinkStatus.Ok, 0);
        }
    }

    /// <summary>
    /// Classifies how a processed job's destination responded, for the link index: a followed
    /// redirect (or any 3xx other than 304) is a redirect; a 2xx or 304 is ok; everything else
    /// (4xx/5xx, or the 500 sentinel used for a fetch error) is an error.
    /// </summary>
    /// <param name="job">The job whose destination outcome to classify.</param>
    /// <returns>The corresponding <see cref="LinkStatus"/>.</returns>
    private static LinkStatus ClassifyLinkStatus(CrawlJob job)
    {
        if (job.RedirectSourceUrl != null) return LinkStatus.Redirect;
        int code = job.StatusCode;
        if (code == 304 || code is >= 200 and < 300) return LinkStatus.Ok;
        if (code is >= 300 and < 400) return LinkStatus.Redirect;
        return LinkStatus.Error;
    }

    /// <summary>
    /// Builds the report's broken and redirected link lists from the link index: rows that resolved
    /// to an error or a redirect this run, narrowed to links found on pages in this crawl's scope
    /// (a shared database may hold other sites' links).
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="crawlStartUtc">The crawl start; only links determined at or after it are reported.</param>
    /// <returns>The broken links and the redirected links, each sorted by origin then destination.</returns>
    private static async Task<(List<BrokenLink> Broken, List<BrokenLink> Redirected)> BuildLinkReportAsync(CrawlContext ctx, DateTime crawlStartUtc)
    {
        var rows = await CrawlStore.GetReportableLinksAsync(ctx.Read, crawlStartUtc, CancellationToken.None);
        var broken = new List<BrokenLink>();
        var redirected = new List<BrokenLink>();
        foreach (var (fromUrl, toUrl, external, status, statusCode) in rows)
        {
            if (!Uri.TryCreate(fromUrl, UriKind.Absolute, out var fromUri) || !ctx.AllowedHosts.IsAllowed(fromUri)) continue;
            var linkStatus = (LinkStatus)status;
            var link = new BrokenLink(toUrl, fromUrl, external, statusCode, ReasonFor(linkStatus, statusCode));
            (linkStatus == LinkStatus.Error ? broken : redirected).Add(link);
        }

        broken.Sort(CompareLinks);
        redirected.Sort(CompareLinks);
        return (broken, redirected);
    }

    /// <summary>Orders report links by the page they were found on, then by the target URL.</summary>
    private static int CompareLinks(BrokenLink a, BrokenLink b)
    {
        int byFound = string.Compare(a.FoundOn, b.FoundOn, StringComparison.OrdinalIgnoreCase);
        return byFound != 0 ? byFound : string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Produces the short human-readable reason shown for a reportable link.</summary>
    /// <param name="status">The link's verified status.</param>
    /// <param name="statusCode">The HTTP status observed (0 for a connection-level failure).</param>
    /// <returns>A short explanation, e.g. "404 Not Found", "connection failed", or "301 redirect".</returns>
    private static string ReasonFor(LinkStatus status, int statusCode)
    {
        if (status == LinkStatus.Redirect)
        {
            return statusCode is >= 300 and < 400 ? $"{statusCode} redirect" : "redirect";
        }
        return statusCode switch
        {
            410 => "410 Gone",
            404 => "404 Not Found",
            0 => "connection failed",
            _ => $"HTTP {statusCode}",
        };
    }

    /// <summary>
    /// Sends a liveness probe: a HEAD request, retried as a header-only GET when the server answers
    /// 405 Method Not Allowed or 501 Not Implemented (servers that don't support HEAD).
    /// </summary>
    /// <param name="url">The URL to probe.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The probe response; the caller owns and disposes it.</returns>
    private async Task<HttpResponseMessage> SendProbeAsync(string url, CancellationToken cancellationToken)
    {
        var head = new HttpRequestMessage(HttpMethod.Head, url);
        var response = await _httpClient.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            response.Dispose();
            var get = new HttpRequestMessage(HttpMethod.Get, url);
            response = await _httpClient.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        return response;
    }

    /// <summary>
    /// Holds the state and tracking information for an active crawl execution.
    /// </summary>
    private sealed class CrawlContext
    {
        /// <summary>Gets the scheme/host/port rules currently in crawl scope.</summary>
        public required AllowedHosts AllowedHosts;

        /// <summary>Gets the cache of parsed robots.txt rules, keyed by origin (scheme://host:port).</summary>
        public required Dictionary<string, RobotsRules> RobotsCache;

        /// <summary>Gets the queue of pending URLs in the frontier.</summary>
        public required Queue<string> Queue;

        /// <summary>Gets the set of URLs already discovered and visited.</summary>
        public required HashSet<string> Visited;

        /// <summary>Gets or sets the seed URL used to initialize the crawl.</summary>
        public string SeedUrl = string.Empty;

        /// <summary>Gets or sets the SQLite database connection used for read operations.</summary>
        public SqliteConnection Read = null!;

        /// <summary>Gets or sets the SQLite database connection used for write operations.</summary>
        public SqliteConnection Write = null!;

        /// <summary>Gets the lookup mapping hostnames to their last fetch timestamps.</summary>
        public Dictionary<string, DateTime> LastFetchUtc = new();

        /// <summary>Gets the lookup tracking the number of pages indexed per host.</summary>
        public Dictionary<string, int> IndexedPerHost = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets the lookup mapping content hashes to the URLs they were first indexed under.</summary>
        public Dictionary<string, string> IndexedContentHashes = new(StringComparer.Ordinal);

        /// <summary>Gets or sets the maximum size in bytes allowed for any single download (pages, files, robots.txt, sitemaps).</summary>
        public long MaxCrawlSizeBytes;

        /// <summary>Gets the origins (scheme://host:port) whose robots.txt was unavailable (5xx) this run; their URLs are exempt from pruning.</summary>
        public HashSet<string> RobotsUnavailable = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets the per-host reachability tracker: which servers have answered this run and which have been written off as unreachable.</summary>
        public HostHealthTracker HostHealth = new();

        /// <summary>Gets or sets a value indicating whether off-site links are probed during the end-of-crawl verification pass and included in the report.</summary>
        public bool CheckExternalLinks;

        /// <summary>Gets or sets a value indicating whether the per-host cap skipped any URL this run, which disables pruning.</summary>
        public bool HostCapSkipped;

        /// <summary>Gets or sets the reporter that receives live progress and phase changes.</summary>
        public required ICrawlObserver Observer;

        /// <summary>Gets or sets the moment the crawl started, used to stamp elapsed time onto snapshots.</summary>
        public required DateTime StartedUtc;


    }
}
