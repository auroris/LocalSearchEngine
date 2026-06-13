using Microsoft.Data.Sqlite;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.Crawling.Extraction;
using LocalSearchEngine.Core.Crawling.Policies;
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
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the crawl operation.</returns>
    public async Task CrawlAsync(string seedUrl, int maxPages = int.MaxValue, IEnumerable<string>? allowedServers = null, int maxPagesPerHost = int.MaxValue, long maxCrawlSizeBytes = 15 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(seedUrl, UriKind.Absolute, out var baseUri))
        {
            _logger.LogError("Invalid seed URL: {Url}", seedUrl);
            return;
        }

        // Stamped before anything is visited: after a crawl that drains naturally, any in-scope
        // row whose LastCrawled predates this moment was unreachable this run and gets pruned.
        var crawlStartUtc = DateTime.UtcNow;

        var ctx = new CrawlContext
        {
            AllowedHosts = new AllowedHosts(),
            RobotsCache = new Dictionary<string, RobotsRules>(StringComparer.OrdinalIgnoreCase),
            Queue = new Queue<string>(),
            Visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            MaxCrawlSizeBytes = maxCrawlSizeBytes,
        };

        if (allowedServers != null)
        {
            foreach (var s in allowedServers)
            {
                if (!ctx.AllowedHosts.Add(s))
                {
                    _logger.LogWarning("Ignoring allowed-server entry '{Entry}': expected [scheme://]host[:port].", s);
                }
            }
        }
        // The seed pins its exact origin: an http seed without an explicit port means http on
        // port 80 only. Note the seed's "www." variant is NOT implied — pass it as an
        // allowed-server entry to crawl both.
        ctx.AllowedHosts.AddOrigin(baseUri);

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

        // Robots for the seed's origin are needed right away (the seed is enqueued below). Every
        // other origin gets its robots fetched lazily on first contact — being listed in the
        // allowed hosts never by itself causes requests to a server.
        var seedRobots = await GetOrFetchRobotsAsync(ctx, baseUri, cancellationToken);

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
                _logger.LogWarning("Seed URL is disallowed by robots.txt: {Url}", normalizedSeed);
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
                    // A skipped URL means "not visited" no longer implies "gone", so this run
                    // must not prune.
                    ctx.HostCapSkipped = true;
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
            if (completedNaturally)
            {
                await PruneStaleUrlsAsync(ctx, crawlStartUtc);
            }
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

        // Frontier filters allow optimistically when an origin's robots aren't cached yet, so
        // the authoritative robots check happens here — after a lazy per-origin robots fetch
        // and before any politeness wait or page request is spent on a disallowed URL.
        var currentRobots = await GetOrFetchRobotsAsync(ctx, currentUri, cancellationToken);
        if (!CrawlPolicy.IsAllowedByRobots(currentUrl, currentRobots))
        {
            _logger.LogInformation("Disallowed by robots.txt: {Url}", currentUrl);
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
                        _logger.LogInformation("Seed {Seed} redirected to {Origin}; adding it to the allowed hosts.", currentUrl, UrlOrigin.Key(finalRequestUri));
                        ctx.AllowedHosts.AddOrigin(finalRequestUri);
                    }
                    else
                    {
                        _logger.LogInformation("Redirect left the allowed hosts: {From} -> {To}", currentUrl, normalizedFinal);
                        return new AliasJob(currentUrl, 302);
                    }
                }
                var finalRobots = await GetOrFetchRobotsAsync(ctx, finalRequestUri, cancellationToken);
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

        // Check Content-Length first
        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > ctx.MaxCrawlSizeBytes)
        {
            _logger.LogWarning("Skipping {Url}: Content-Length ({Length} bytes) exceeds maximum limit of {Limit} bytes.", finalUrl, contentLength.Value, ctx.MaxCrawlSizeBytes);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        // Check Content-Type first
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!CrawlPolicy.IsSupportedOrGenericContentType(contentType))
        {
            _logger.LogInformation("Skipping {Url}: Content-Type '{ContentType}' is not whitelisted for indexing.", finalUrl, contentType);
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
                    _logger.LogWarning("Skipping {Url}: content size exceeded limit of {Limit} bytes during download.", finalUrl, ctx.MaxCrawlSizeBytes);
                    return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
                }

                bodyStream.Write(buffer, 0, bytesRead);

                if (!checkedPrefix && bodyStream.Length >= 4096)
                {
                    checkedPrefix = true;
                    var prefix = bodyStream.ToArray();
                    if (!CrawlPolicy.IsSupportedPrefix(prefix, contentType, finalUrl))
                    {
                        _logger.LogInformation("Skipping {Url}: content structure (magic-byte sniff) is not supported.", finalUrl);
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
                    _logger.LogInformation("Skipping {Url}: content structure (magic-byte sniff) is not supported.", finalUrl);
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
    /// Removes index entries for in-scope URLs the completed crawl never reached. Runs only
    /// after a crawl that drained its frontier naturally, where "not visited this run" reliably
    /// means "no longer reachable": orphaned pages whose links were removed, and pages a robots
    /// rule now disallows. Rows on hosts outside this crawl's scope (e.g. another site sharing
    /// the database) are never touched, and origins whose robots.txt was unavailable (5xx) this
    /// run are exempt — their URLs went unvisited for reasons that say nothing about staleness.
    /// </summary>
    /// <param name="ctx">The active crawl context.</param>
    /// <param name="crawlStartUtc">The crawl's start time; rows last visited before it are stale.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task PruneStaleUrlsAsync(CrawlContext ctx, DateTime crawlStartUtc)
    {
        try
        {
            var candidates = await CrawlStore.GetUrlsNotCrawledSinceAsync(ctx.Read, crawlStartUtc, CancellationToken.None);
            int pruned = 0;
            foreach (var url in candidates)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                if (!ctx.AllowedHosts.IsAllowed(uri)) continue;
                if (ctx.RobotsUnavailable.Contains(UrlOrigin.Key(uri))) continue;

                await _vectorSearchService.DeleteUrlChunksAsync(url);
                await CrawlStore.DeleteOutlinksAsync(ctx.Write, url, CancellationToken.None);
                await CrawlStore.DeleteCrawlStateAsync(ctx.Write, url, CancellationToken.None);
                pruned++;
            }
            if (pruned > 0)
            {
                _logger.LogInformation("Pruned {Count} stale URLs the completed crawl no longer reaches.", pruned);
            }
        }
        catch (Exception ex)
        {
            // Pruning is housekeeping; a failure here must not turn a finished crawl into an error.
            _logger.LogError(ex, "Failed to prune stale URLs.");
        }
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
            if (!ctx.AllowedHosts.IsAllowed(uri)) continue;
            var robots = ctx.RobotsCache.TryGetValue(UrlOrigin.Key(uri), out var r) ? r : RobotsRules.AllowAll;
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
            // Honor Crawl-delay, but clamp it: a huge value (misconfigured or hostile
            // robots.txt) must not be able to stall the crawl for minutes per page.
            ms = Math.Max(ms, Math.Min(seconds, MaxCrawlDelaySeconds) * 1000);
        }
        return TimeSpan.FromMilliseconds(ms);
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

        /// <summary>Gets or sets a value indicating whether the per-host cap skipped any URL this run, which disables pruning.</summary>
        public bool HostCapSkipped;
    }
}
