using Microsoft.Data.Sqlite;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

using LocalSearchEngine.Core.Searching;

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
    /// Orchestrates the crawl loop starting from a seed URL.
    /// </summary>
    /// <param name="seedUrl">The starting URL of the crawl.</param>
    /// <param name="maxPages">The maximum number of pages to index.</param>
    /// <param name="allowedServers">Optional set of allowed hostnames.</param>
    /// <param name="maxPagesPerHost">The maximum pages to crawl on any single host.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the crawl operation.</returns>
    public async Task CrawlAsync(string seedUrl, int maxPages = int.MaxValue, IEnumerable<string>? allowedServers = null, int maxPagesPerHost = int.MaxValue, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(seedUrl, UriKind.Absolute, out var baseUri))
        {
            _logger.LogError("Invalid seed URL: {Url}", seedUrl);
            return;
        }

        var ctx = new CrawlContext
        {
            AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            RobotsCache = new Dictionary<string, RobotsRules>(StringComparer.OrdinalIgnoreCase),
            Queue = new Queue<string>(),
            Visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };

        if (allowedServers != null)
        {
            foreach (var s in allowedServers) ctx.AllowedHosts.Add(s);
        }
        ctx.AllowedHosts.Add(baseUri.Host);
        ctx.AllowedHosts.Add("www." + baseUri.Host);

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

        foreach (var host in ctx.AllowedHosts)
        {
            var hostUri = new Uri($"{baseUri.Scheme}://{host}");
            ctx.RobotsCache[host] = await GetRobotsRulesAsync(hostUri, cancellationToken);
        }

        // Resume an interrupted run, then top up from sitemaps and the seed.
        await RestoreFrontierAsync(ctx, cancellationToken);

        foreach (var host in ctx.AllowedHosts)
        {
            var hostUri = new Uri($"{baseUri.Scheme}://{host}");
            await EnqueueSitemapUrlsAsync(hostUri, ctx, cancellationToken);
        }

        var normalizedSeed = UrlNormalizer.Normalize(baseUri);
        ctx.SeedUrl = normalizedSeed;
        if (ctx.Visited.Add(normalizedSeed))
        {
            if (CrawlPolicy.IsAllowedByRobots(normalizedSeed, ctx.RobotsCache[baseUri.Host]))
            {
                ctx.Queue.Enqueue(normalizedSeed);
            }
            else
            {
                _logger.LogWarning("Seed URL is disallowed by robots.txt: {Url}", normalizedSeed);
            }
        }

        int indexedCount = 0;
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
            while (ctx.Queue.Count > 0 && indexedCount < maxPages)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Crawl cancelled after dispatching {Indexed} pages.", indexedCount);
                    break;
                }

                var currentUrl = ctx.Queue.Dequeue();

                // Safety valve against crawler traps (calendars, faceted nav): once a host has
                // contributed its cap of indexed pages, stop fetching more of its URLs.
                if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentHostUri)
                    && ctx.IndexedPerHost.TryGetValue(currentHostUri.Host, out var hostIndexed)
                    && hostIndexed >= maxPagesPerHost)
                {
                    _logger.LogInformation("Per-host cap ({Cap}) reached for {Host}; skipping {Url}", maxPagesPerHost, currentHostUri.Host, currentUrl);
                    continue;
                }

                _logger.LogInformation("Crawling ({Indexed} indexed / {Discovered} discovered): {Url}", indexedCount, ctx.Visited.Count, currentUrl);

                CrawlJob? job;
                try
                {
                    job = await ProduceJobAsync(ctx, currentUrl, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Put the in-flight page back so a resumed run retries it.
                    ctx.Queue.Enqueue(currentUrl);
                    _logger.LogInformation("Crawl cancelled while fetching {Url}.", currentUrl);
                    break;
                }
                catch (Exception ex)
                {
                    // Fetch/parse failed unexpectedly: note the visit but KEEP any content
                    // already indexed for this URL — a transient failure must not erase data.
                    _logger.LogError(ex, "Error occurred while crawling {Url}", currentUrl);
                    job = new TouchJob(currentUrl, 500);
                }

                if (job is not null)
                {
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
        }
        finally
        {
            // Stop the indexer, let it drain everything already fetched, then snapshot the
            // remaining frontier (resumable) and tidy the database.
            channel.Writer.Complete();
            await indexer;
            await PersistFrontierAsync(ctx, CancellationToken.None);
            await CrawlStore.OptimizeDatabaseAsync(ctx.Write, _logger);
        }

        _logger.LogInformation("Crawling completed for {SeedUrl} ({Indexed} pages indexed this run).", seedUrl, indexedCount);
    }

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

        var currentRobots = ctx.RobotsCache.TryGetValue(currentUri.Host, out var r) ? r : RobotsRules.AllowAll;
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
        int statusCode = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            // Unchanged: re-derive the frontier from this page's stored outlinks so the crawl
            // can still reach pages only linked from here.
            _logger.LogInformation("Page not modified since last crawl (304): {Url}", currentUrl);
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
                // the source URL (rather than returning null), so it gets a CrawlState row and a
                // resumed run doesn't keep re-fetching it from scratch.
                if (!ctx.AllowedHosts.Contains(finalRequestUri.Host))
                {
                    // The seed itself redirecting to a new host (a vanity domain, or a redirect
                    // landing on a host we didn't anticipate) means the site really lives there:
                    // adopt that host into scope and fetch its robots, instead of ending the crawl
                    // at the front door. Off-host redirects from any *other* page stay rejected.
                    if (string.Equals(currentUrl, ctx.SeedUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Seed {Seed} redirected to {Host}; adding it to the allowed hosts.", currentUrl, finalRequestUri.Host);
                        ctx.AllowedHosts.Add(finalRequestUri.Host);
                        if (!ctx.RobotsCache.ContainsKey(finalRequestUri.Host))
                        {
                            var newHostUri = new Uri($"{finalRequestUri.Scheme}://{finalRequestUri.Host}");
                            ctx.RobotsCache[finalRequestUri.Host] = await GetRobotsRulesAsync(newHostUri, cancellationToken);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Redirect left the allowed hosts: {From} -> {To}", currentUrl, normalizedFinal);
                        return new AliasJob(currentUrl, 302);
                    }
                }
                var finalRobots = ctx.RobotsCache.TryGetValue(finalRequestUri.Host, out var fr) ? fr : RobotsRules.AllowAll;
                if (!CrawlPolicy.IsAllowedByRobots(normalizedFinal, finalRobots))
                {
                    _logger.LogInformation("Redirect target disallowed by robots.txt: {Url}", normalizedFinal);
                    return new AliasJob(currentUrl, 302);
                }
                if (!ctx.Visited.Add(normalizedFinal))
                {
                    _logger.LogInformation("Redirected to already-seen URL: {Url}", normalizedFinal);
                    return new AliasJob(currentUrl, 302);
                }
                finalUrl = normalizedFinal;
            }
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Genuinely gone: drop it from the index.
            _logger.LogInformation("Page gone ({StatusCode}): {Url} — removing from index.", statusCode, finalUrl);
            return new GoneJob(finalUrl, statusCode, redirectSourceUrl);
        }

        if (!response.IsSuccessStatusCode)
        {
            // 5xx / 403 / etc.: keep whatever is already indexed; just record the visit.
            _logger.LogWarning("Failed to crawl {Url} with status code {StatusCode}; keeping existing index.", finalUrl, statusCode);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

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
            _logger.LogInformation("Content unchanged since last crawl (hash match): {Url}", finalUrl);
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
            _logger.LogInformation("Duplicate content: {Url} matches already-indexed {Canonical}; not indexing a copy.", finalUrl, duplicateOf);
            EnqueueSingle(ctx, duplicateOf);
            return new AliasJob(finalUrl, statusCode, redirectSourceUrl);
        }

        string? newETag = response.Headers.ETag?.Tag;
        string? newLastModified = response.Content.Headers.LastModified?.ToString("r");

        // How to parse the body is decided by the server-declared Content-Type (falling back to
        // byte sniffing), never the URL's file extension — see CrawlPolicy.ClassifyContent.
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var kind = CrawlPolicy.ClassifyContent(contentType, body);

        if (kind == DocKind.Pdf)
        {
            var (pdfTitle, pdfText) = ContentExtractor.ExtractPdf(body);
            // Surface the embedded document title as both the stored title and a heading chunk,
            // mirroring how an HTML <title> is indexed, so documents aren't bare URLs in results.
            ctx.IndexedContentHashes[newHash] = finalUrl;
            return new IndexJob(finalUrl, statusCode, pdfTitle, pdfTitle ?? string.Empty, pdfText,
                newETag, newLastModified, newHash, Array.Empty<string>(), redirectSourceUrl);
        }

        if (kind == DocKind.Docx)
        {
            var (docxTitle, docxText) = ContentExtractor.ExtractDocx(body);
            ctx.IndexedContentHashes[newHash] = finalUrl;
            return new IndexJob(finalUrl, statusCode, docxTitle, docxTitle ?? string.Empty, docxText,
                newETag, newLastModified, newHash, Array.Empty<string>(), redirectSourceUrl);
        }

        // Not a document we extract and not HTML (by Content-Type or sniff): skip it, so JSON,
        // images, etc. served at extensionless or dynamic routes don't get parsed as a web page.
        if (kind != DocKind.Html)
        {
            _logger.LogInformation("Skipping {Url}: unindexable content type '{ContentType}'.", finalUrl, contentType);
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
            _logger.LogInformation("Canonical alias: {Url} -> {Canonical}", finalUrl, analysis.CanonicalAlias);
            EnqueueSingle(ctx, analysis.CanonicalAlias);
            // No ContentHash stored, so the alias is re-evaluated (and the canonical re-queued)
            // on each crawl.
            return new AliasJob(finalUrl, statusCode, redirectSourceUrl);
        }

        // Enqueue newly discovered links now (producer-owned frontier); the indexer persists
        // this page's outlink set for future 304/unchanged re-crawls.
        foreach (var link in analysis.Outlinks)
        {
            if (ctx.Visited.Add(link)) ctx.Queue.Enqueue(link);
        }

        if (analysis.NoIndex)
        {
            // Respect noindex: ensure it isn't in the index, but keep crawl state + outlinks.
            _logger.LogInformation("noindex directive: {Url} — not indexing its content.", finalUrl);
            return new NoIndexJob(finalUrl, statusCode, analysis.Title, newETag, newLastModified, newHash, analysis.Outlinks, redirectSourceUrl);
        }

        ctx.IndexedContentHashes[newHash] = finalUrl;
        return new IndexJob(finalUrl, statusCode, analysis.Title, analysis.Headings, analysis.Text,
            newETag, newLastModified, newHash, analysis.Outlinks, redirectSourceUrl);
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
                    await CrawlStore.DeleteOutlinksAsync(connection, job.RedirectSourceUrl, CancellationToken.None);
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
                        await CrawlStore.StoreOutlinksAsync(connection, j.Url, j.Outlinks, CancellationToken.None);
                        _logger.LogInformation("Indexed {Url} ({Links} outlinks).", j.Url, j.Outlinks.Count);
                        break;

                    case NoIndexJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await CrawlStore.StoreOutlinksAsync(connection, j.Url, j.Outlinks, CancellationToken.None);
                        await CrawlStore.RecordCrawlStateAsync(connection, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, CancellationToken.None);
                        break;

                    case GoneJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await CrawlStore.DeleteOutlinksAsync(connection, j.Url, CancellationToken.None);
                        await CrawlStore.RecordVisitAsync(connection, j.Url, j.StatusCode, clearMetadata: true, CancellationToken.None);
                        break;

                    case AliasJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await CrawlStore.DeleteOutlinksAsync(connection, j.Url, CancellationToken.None);
                        await CrawlStore.RecordVisitAsync(connection, j.Url, j.StatusCode, clearMetadata: true, CancellationToken.None);
                        break;

                    case TouchJob j:
                        await CrawlStore.RecordVisitAsync(connection, j.Url, j.StatusCode, clearMetadata: false, CancellationToken.None);
                        break;
                }
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
        string? RedirectSourceUrl = null)
        : CrawlJob(Url, StatusCode, RedirectSourceUrl);

    /// <summary>
    /// Represents a job classification where indexing is skipped but crawl state and outlinks are stored.
    /// </summary>
    private sealed record NoIndexJob(
        string Url, int StatusCode, string? Title, string? ETag, string? LastModified,
        string ContentHash, IReadOnlyCollection<string> Outlinks, string? RedirectSourceUrl = null)
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
    /// Validates and enqueues a single URL into the frontier queue.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="url">The URL string to enqueue.</param>
    private void EnqueueSingle(CrawlContext ctx, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (!ctx.AllowedHosts.Contains(uri.Host)) return;
        var robots = ctx.RobotsCache.TryGetValue(uri.Host, out var r) ? r : RobotsRules.AllowAll;
        if (!CrawlPolicy.IsAllowedByRobots(url, robots)) return;
        if (ctx.Visited.Add(url)) ctx.Queue.Enqueue(url);
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
            if (!ctx.AllowedHosts.Contains(uri.Host)) continue;
            if (!CrawlPolicy.IsIndexableExtension(link)) continue;
            var robots = ctx.RobotsCache.TryGetValue(uri.Host, out var r) ? r : RobotsRules.AllowAll;
            if (!CrawlPolicy.IsAllowedByRobots(link, robots)) continue;
            if (ctx.Visited.Add(link)) ctx.Queue.Enqueue(link);
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
            ms = Math.Max(ms, seconds * 1000);
        }
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// Restores the frontier queue and visited set from a saved database snapshot.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the restore operation.</returns>
    private async Task RestoreFrontierAsync(CrawlContext ctx, CancellationToken cancellationToken)
    {
        var resumed = await CrawlStore.ReadFrontierAsync(ctx.Write, cancellationToken);
        if (resumed.Count == 0) return;

        foreach (var url in resumed)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            if (!ctx.AllowedHosts.Contains(uri.Host)) continue;
            var robots = ctx.RobotsCache.TryGetValue(uri.Host, out var r) ? r : RobotsRules.AllowAll;
            if (!CrawlPolicy.IsAllowedByRobots(url, robots)) continue;
            if (ctx.Visited.Add(url)) ctx.Queue.Enqueue(url);
        }

        // We've taken ownership of the snapshot; clear it (a fresh one is written at the end).
        await CrawlStore.ClearFrontierAsync(ctx.Write, cancellationToken);

        _logger.LogInformation("Resumed {Count} URLs from a previously interrupted crawl.", ctx.Queue.Count);
    }

    /// <summary>
    /// Saves the current frontier queue to the database to support resuming later.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the persistence operation.</returns>
    private async Task PersistFrontierAsync(CrawlContext ctx, CancellationToken cancellationToken)
    {
        await CrawlStore.SaveFrontierAsync(ctx.Write, ctx.Queue, cancellationToken);
        if (ctx.Queue.Count > 0)
        {
            _logger.LogInformation("Saved {Count} pending URLs to resume next run.", ctx.Queue.Count);
        }
    }

    /// <summary>
    /// Holds the state and tracking information for an active crawl execution.
    /// </summary>
    private sealed class CrawlContext
    {
        /// <summary>Gets the set of hostnames currently in crawl scope.</summary>
        public required HashSet<string> AllowedHosts;

        /// <summary>Gets the cache containing parsed robots.txt rules for visited hosts.</summary>
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
    }
}
