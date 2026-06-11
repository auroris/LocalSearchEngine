using HtmlAgilityPack;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace LocalSearchEngine.Core;

public class CrawlerService
{
    /// <summary>Identifies this crawler in request headers and for robots.txt matching.</summary>
    public const string UserAgent = "LocalSearchEngine-Bot/1.0";

    /// <summary>Lowercased token used to match our own <c>&lt;meta name="..."&gt;</c> robots rules.</summary>
    private const string UserAgentToken = "localsearchengine-bot";

    /// <summary>Hard cap on a single response body; larger documents are skipped.</summary>
    private const long MaxDownloadBytes = 25L * 1024 * 1024;

    /// <summary>Minimum politeness gap between requests to the same host.</summary>
    private const int DefaultRequestDelayMs = 250;

    private static readonly char[] WordSeparators = { ' ', '\n', '\r', '\t' };

    private static readonly Regex CharsetRegex = new(
        "charset\\s*=\\s*[\"']?([a-zA-Z0-9_\\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static CrawlerService()
    {
        // Lets Encoding.GetEncoding resolve legacy labels (windows-1252, etc.) when a page
        // declares one via <meta charset>. Without this provider only a few encodings exist.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly HttpClient _httpClient;
    private readonly VectorSearchService _vectorSearchService;
    private readonly ILogger<CrawlerService> _logger;
    private readonly string _connectionString;

    public CrawlerService(HttpClient httpClient, VectorSearchService vectorSearchService, ILogger<CrawlerService> logger, DatabaseConfig dbConfig)
    {
        _httpClient = httpClient;
        _vectorSearchService = vectorSearchService;
        _logger = logger;
        _connectionString = dbConfig.ConnectionString;
    }

    public async Task EnsureCreatedAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // The FTS triggers below reference the vector store's data table, which is
        // created by VectorSearchService.EnsureCreatedAsync(). Fail loudly if that
        // step was skipped (or a package upgrade changed the table name).
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='text_chunks'";
            if (await check.ExecuteScalarAsync() is null)
            {
                throw new InvalidOperationException(
                    "The 'text_chunks' table is missing. Call VectorSearchService.EnsureCreatedAsync() before CrawlerService.EnsureCreatedAsync().");
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                PRAGMA journal_mode=WAL;

                CREATE TABLE IF NOT EXISTS CrawlState (
                    Url TEXT PRIMARY KEY,
                    LastCrawled DATETIME,
                    StatusCode INTEGER,
                    ETag TEXT,
                    LastModified TEXT,
                    Title TEXT,
                    ContentHash TEXT
                );

                -- Outlinks per page, so an incremental re-crawl can keep growing the frontier
                -- even when a page returns 304/unchanged (we never re-parse its HTML then).
                CREATE TABLE IF NOT EXISTS CrawlLinks (
                    FromUrl TEXT NOT NULL,
                    ToUrl TEXT NOT NULL,
                    PRIMARY KEY (FromUrl, ToUrl)
                );
                CREATE INDEX IF NOT EXISTS idx_crawllinks_from ON CrawlLinks(FromUrl);

                -- A snapshot of the pending queue, written when a run is interrupted so the
                -- next run can resume instead of starting the whole site over.
                CREATE TABLE IF NOT EXISTS CrawlFrontier (
                    Url TEXT PRIMARY KEY,
                    Seq INTEGER
                );

                -- porter stemming over unicode61 so 'running' matches 'run', 'guides' matches
                -- 'guide', etc.
                CREATE VIRTUAL TABLE IF NOT EXISTS text_chunks_fts USING fts5(Id UNINDEXED, Url UNINDEXED, Text, tokenize='porter unicode61');

                CREATE TRIGGER IF NOT EXISTS text_chunks_ai AFTER INSERT ON text_chunks BEGIN
                  INSERT INTO text_chunks_fts(Id, Url, Text) VALUES (new.Id, new.Url, new.Text);
                END;

                CREATE TRIGGER IF NOT EXISTS text_chunks_ad AFTER DELETE ON text_chunks BEGIN
                  DELETE FROM text_chunks_fts WHERE Id = old.Id;
                END;

                CREATE TRIGGER IF NOT EXISTS text_chunks_au AFTER UPDATE ON text_chunks BEGIN
                  DELETE FROM text_chunks_fts WHERE Id = old.Id;
                  INSERT INTO text_chunks_fts(Id, Url, Text) VALUES (new.Id, new.Url, new.Text);
                END;
            ";
            await command.ExecuteNonQueryAsync();
        }

        // Bring forward databases created before Title/ContentHash existed.
        await EnsureColumnAsync(connection, "CrawlState", "Title", "TEXT");
        await EnsureColumnAsync(connection, "CrawlState", "ContentHash", "TEXT");
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string type)
    {
        bool exists;
        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @c";
            check.Parameters.AddWithValue("@c", column);
            exists = await check.ExecuteScalarAsync() is not null;
        }
        if (!exists)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            await alter.ExecuteNonQueryAsync();
        }
    }

    public async Task CrawlAsync(string seedUrl, int maxPages = int.MaxValue, IEnumerable<string>? allowedServers = null, CancellationToken cancellationToken = default)
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
        if (ctx.Visited.Add(normalizedSeed))
        {
            if (IsAllowedByRobots(normalizedSeed, ctx.RobotsCache[baseUri.Host]))
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
        var indexer = ConsumeAsync(channel.Reader);

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
                    if (job is IndexJob) indexedCount++;
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
            await OptimizeDatabaseAsync();
        }

        _logger.LogInformation("Crawling completed for {SeedUrl} ({Indexed} pages indexed this run).", seedUrl, indexedCount);
    }

    /// <summary>
    /// Fetches and analyzes a single URL on the producer side (network + parse + frontier
    /// growth, all DB reads only), returning the unit of work for the indexer to persist — or
    /// null when there is nothing to write (out-of-scope redirect, oversized body, etc.).
    /// </summary>
    private async Task<CrawlJob?> ProduceJobAsync(CrawlContext ctx, string currentUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri)) return null;

        var currentRobots = ctx.RobotsCache.TryGetValue(currentUri.Host, out var r) ? r : RobotsRules.AllowAll;
        await DelayForHostAsync(ctx, currentUri.Host, ResolveRequestDelay(currentRobots), cancellationToken);

        var state = await GetCrawlStateAsync(currentUrl, cancellationToken);

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
        var finalRequestUri = response.RequestMessage?.RequestUri;
        if (finalRequestUri != null)
        {
            var normalizedFinal = UrlNormalizer.Normalize(finalRequestUri);
            if (!string.Equals(normalizedFinal, currentUrl, StringComparison.OrdinalIgnoreCase))
            {
                if (!ctx.AllowedHosts.Contains(finalRequestUri.Host))
                {
                    _logger.LogInformation("Redirect left the allowed hosts: {From} -> {To}", currentUrl, normalizedFinal);
                    return null;
                }
                var finalRobots = ctx.RobotsCache.TryGetValue(finalRequestUri.Host, out var fr) ? fr : RobotsRules.AllowAll;
                if (!IsAllowedByRobots(normalizedFinal, finalRobots))
                {
                    _logger.LogInformation("Redirect target disallowed by robots.txt: {Url}", normalizedFinal);
                    return null;
                }
                if (!ctx.Visited.Add(normalizedFinal))
                {
                    _logger.LogInformation("Redirected to already-seen URL: {Url}", normalizedFinal);
                    return null;
                }
                finalUrl = normalizedFinal;
            }
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Genuinely gone: drop it from the index.
            _logger.LogInformation("Page gone ({StatusCode}): {Url} — removing from index.", statusCode, finalUrl);
            return new GoneJob(finalUrl, statusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            // 5xx / 403 / etc.: keep whatever is already indexed; just record the visit.
            _logger.LogWarning("Failed to crawl {Url} with status code {StatusCode}; keeping existing index.", finalUrl, statusCode);
            return new TouchJob(finalUrl, statusCode);
        }

        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            _logger.LogWarning("Skipping {Url}: content length {Length} exceeds limit {Limit}", finalUrl, response.Content.Headers.ContentLength, MaxDownloadBytes);
            return null;
        }

        var body = await ReadBoundedAsync(response.Content, MaxDownloadBytes, cancellationToken);
        if (body is null)
        {
            _logger.LogWarning("Skipping {Url}: response body exceeds limit {Limit}", finalUrl, MaxDownloadBytes);
            return null;
        }

        // Byte-identical to the last successful crawl? Treat exactly like a 304 — re-enqueue
        // stored outlinks, skip the expensive re-embed.
        string newHash = ComputeHash(body);
        if (state.ContentHash is not null && state.ContentHash == newHash)
        {
            _logger.LogInformation("Content unchanged since last crawl (hash match): {Url}", finalUrl);
            await EnqueueStoredOutlinksAsync(ctx, finalUrl, cancellationToken);
            return new TouchJob(finalUrl, statusCode);
        }

        string? newETag = response.Headers.ETag?.Tag;
        string? newLastModified = response.Content.Headers.LastModified?.ToString("r");
        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        var extension = Path.GetExtension(finalUrl.Split('?')[0])?.ToLowerInvariant();

        if ((contentType != null && contentType.Contains("pdf")) || extension == ".pdf")
        {
            return new IndexJob(finalUrl, statusCode, Title: null, Headings: string.Empty, ExtractPdfText(body),
                newETag, newLastModified, newHash, Array.Empty<string>());
        }

        if ((contentType != null && contentType.Contains("wordprocessingml")) || extension == ".docx")
        {
            return new IndexJob(finalUrl, statusCode, Title: null, Headings: string.Empty, ExtractDocxText(body),
                newETag, newLastModified, newHash, Array.Empty<string>());
        }

        // Anything that isn't a known document type and isn't HTML (by declared content type
        // or .html/.htm extension) is skipped, so JSON/images at extensionless routes don't
        // get parsed as a web page.
        bool htmlByExtension = extension is ".html" or ".htm";
        if (!IsHtmlContentType(contentType) && !htmlByExtension)
        {
            _logger.LogInformation("Skipping {Url}: non-HTML content type '{ContentType}'.", finalUrl, contentType);
            return new TouchJob(finalUrl, statusCode);
        }

        var xRobotsTag = response.Headers.TryGetValues("X-Robots-Tag", out var values)
            ? string.Join(",", values)
            : null;
        var analysis = AnalyzeHtml(body, response.Content.Headers.ContentType?.CharSet, xRobotsTag, finalUrl, ctx);

        // A canonical link pointing elsewhere marks this URL as an alias: crawl the canonical
        // copy instead and don't index a duplicate here.
        if (analysis.CanonicalAlias != null)
        {
            _logger.LogInformation("Canonical alias: {Url} -> {Canonical}", finalUrl, analysis.CanonicalAlias);
            EnqueueSingle(ctx, analysis.CanonicalAlias);
            // No ContentHash stored, so the alias is re-evaluated (and the canonical re-queued)
            // on each crawl.
            return new AliasJob(finalUrl, statusCode);
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
            return new NoIndexJob(finalUrl, statusCode, analysis.Title, newETag, newLastModified, newHash, analysis.Outlinks);
        }

        return new IndexJob(finalUrl, statusCode, analysis.Title, analysis.Headings, analysis.Text,
            newETag, newLastModified, newHash, analysis.Outlinks);
    }

    /// <summary>
    /// The single indexer: drains fetched work and applies it to the database. Being the only
    /// consumer, its writes (and embeddings) run one at a time, which is what lets the producer
    /// fetch concurrently without violating SQLite's single-writer constraint.
    /// </summary>
    private async Task ConsumeAsync(ChannelReader<CrawlJob> reader)
    {
        await foreach (var job in reader.ReadAllAsync())
        {
            try
            {
                switch (job)
                {
                    case IndexJob j:
                        await ReindexAsync(j.Url, j.StatusCode, j.Title, j.Headings, j.Text, j.ETag, j.LastModified, j.ContentHash, CancellationToken.None);
                        await StoreOutlinksAsync(j.Url, j.Outlinks, CancellationToken.None);
                        _logger.LogInformation("Indexed {Url} ({Links} outlinks).", j.Url, j.Outlinks.Count);
                        break;

                    case NoIndexJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await StoreOutlinksAsync(j.Url, j.Outlinks, CancellationToken.None);
                        await RecordCrawlStateAsync(j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, CancellationToken.None);
                        break;

                    case GoneJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await DeleteOutlinksAsync(j.Url, CancellationToken.None);
                        await RecordVisitAsync(j.Url, j.StatusCode, CancellationToken.None);
                        break;

                    case AliasJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await RecordVisitAsync(j.Url, j.StatusCode, CancellationToken.None);
                        break;

                    case TouchJob j:
                        await RecordVisitAsync(j.Url, j.StatusCode, CancellationToken.None);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply crawl result for {Url}", job.Url);
            }
        }
    }

    private abstract record CrawlJob(string Url, int StatusCode);

    private sealed record IndexJob(
        string Url, int StatusCode, string? Title, string Headings, string Text,
        string? ETag, string? LastModified, string ContentHash, IReadOnlyCollection<string> Outlinks)
        : CrawlJob(Url, StatusCode);

    private sealed record NoIndexJob(
        string Url, int StatusCode, string? Title, string? ETag, string? LastModified,
        string ContentHash, IReadOnlyCollection<string> Outlinks)
        : CrawlJob(Url, StatusCode);

    private sealed record GoneJob(string Url, int StatusCode) : CrawlJob(Url, StatusCode);

    private sealed record AliasJob(string Url, int StatusCode) : CrawlJob(Url, StatusCode);

    private sealed record TouchJob(string Url, int StatusCode) : CrawlJob(Url, StatusCode);

    /// <summary>Replaces any previously indexed chunks for a URL and records fresh crawl state.</summary>
    private async Task ReindexAsync(string url, int statusCode, string? title, string headings, string text,
        string? etag, string? lastModified, string contentHash, CancellationToken cancellationToken)
    {
        await _vectorSearchService.DeleteUrlChunksAsync(url);
        await _vectorSearchService.IndexUrlChunksAsync(url, text, isHeading: false);
        if (!string.IsNullOrWhiteSpace(headings))
        {
            await _vectorSearchService.IndexUrlChunksAsync(url, headings, isHeading: true);
        }
        await RecordCrawlStateAsync(url, statusCode, etag, lastModified, title, contentHash, cancellationToken);
    }

    private sealed class HtmlAnalysis
    {
        public string? Title;
        public string Headings = string.Empty;
        public string Text = string.Empty;
        public bool NoIndex;
        public bool NoFollow;
        public string? CanonicalAlias;
        public List<string> Outlinks = new();
    }

    private HtmlAnalysis AnalyzeHtml(byte[] body, string? httpCharset, string? xRobotsTag, string currentUrl, CrawlContext ctx)
    {
        var html = DecodeHtml(body, httpCharset);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var analysis = new HtmlAnalysis
        {
            Title = ExtractTitle(doc),
        };

        var (noIndex, noFollow) = ParseRobotsDirectives(doc, xRobotsTag);
        analysis.NoIndex = noIndex;
        analysis.NoFollow = noFollow;
        analysis.CanonicalAlias = ResolveCanonicalAlias(doc, currentUrl, ctx.AllowedHosts);

        // Strip boilerplate BEFORE harvesting headings/text/links so footer "Quick Links"
        // headings and nav chrome don't pollute the index.
        var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header|//svg");
        if (nodesToRemove != null)
        {
            foreach (var node in nodesToRemove) node.Remove();
        }

        analysis.Headings = ExtractHeadings(doc);
        analysis.Text = ExtractVisibleText(doc.DocumentNode);

        if (!analysis.NoFollow)
        {
            analysis.Outlinks = ExtractInScopeLinks(doc, currentUrl, ctx);
        }

        return analysis;
    }

    /// <summary>In-scope, indexable, robots-allowed, non-nofollow links found on a page.</summary>
    private List<string> ExtractInScopeLinks(HtmlDocument doc, string currentUrl, CrawlContext ctx)
    {
        var result = new List<string>();
        var linkNodes = doc.DocumentNode.SelectNodes("//a[@href]");
        if (linkNodes is null) return result;

        var baseForLinks = new Uri(currentUrl);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in linkNodes)
        {
            var rel = link.GetAttributeValue("rel", string.Empty);
            if (rel.Contains("nofollow", StringComparison.OrdinalIgnoreCase)) continue;

            var href = link.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(baseForLinks, href, out var absoluteUri)) continue;
            if (!ctx.AllowedHosts.Contains(absoluteUri.Host)) continue;

            var normalizedUrl = UrlNormalizer.Normalize(absoluteUri);
            if (!CrawlPolicy.IsIndexableExtension(normalizedUrl)) continue;

            var linkRobots = ctx.RobotsCache.TryGetValue(absoluteUri.Host, out var lr) ? lr : RobotsRules.AllowAll;
            if (!IsAllowedByRobots(normalizedUrl, linkRobots)) continue;

            if (seen.Add(normalizedUrl)) result.Add(normalizedUrl);
        }

        return result;
    }

    private static (bool NoIndex, bool NoFollow) ParseRobotsDirectives(HtmlDocument doc, string? xRobotsTag)
    {
        bool noIndex = false, noFollow = false;

        void Apply(string content)
        {
            foreach (var raw in content.Split(','))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "none": noIndex = true; noFollow = true; break;
                    case "noindex": noIndex = true; break;
                    case "nofollow": noFollow = true; break;
                }
            }
        }

        var metas = doc.DocumentNode.SelectNodes("//meta[@name]");
        if (metas != null)
        {
            foreach (var meta in metas)
            {
                var name = meta.GetAttributeValue("name", string.Empty).Trim().ToLowerInvariant();
                if (name == "robots" || name == UserAgentToken)
                {
                    Apply(HtmlEntity.DeEntitize(meta.GetAttributeValue("content", string.Empty)));
                }
            }
        }

        if (!string.IsNullOrEmpty(xRobotsTag))
        {
            Apply(StripXRobotsAgent(xRobotsTag));
        }

        return (noIndex, noFollow);
    }

    /// <summary>
    /// Strips an optional bot-name prefix from an X-Robots-Tag value, honoring it only when
    /// it is unscoped or scoped to this crawler.
    /// </summary>
    private static string StripXRobotsAgent(string value)
    {
        int colon = value.IndexOf(':');
        if (colon < 0) return value;

        var prefix = value[..colon].Trim().ToLowerInvariant();
        // No agent prefix — the token before ':' is itself a directive.
        if (prefix is "noindex" or "nofollow" or "none" or "all" or "index" or "follow") return value;
        return prefix == UserAgentToken ? value[(colon + 1)..] : string.Empty;
    }

    private static string? ResolveCanonicalAlias(HtmlDocument doc, string currentUrl, HashSet<string> allowedHosts)
    {
        var links = doc.DocumentNode.SelectNodes("//link[@rel]");
        if (links is null) return null;

        foreach (var link in links)
        {
            if (!string.Equals(link.GetAttributeValue("rel", string.Empty).Trim(), "canonical", StringComparison.OrdinalIgnoreCase))
                continue;

            var href = link.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href)) return null;
            if (!Uri.TryCreate(new Uri(currentUrl), href, out var canonicalUri)) return null;

            var normalized = UrlNormalizer.Normalize(canonicalUri);
            if (string.Equals(normalized, currentUrl, StringComparison.OrdinalIgnoreCase)) return null; // self-canonical
            if (!allowedHosts.Contains(canonicalUri.Host)) return null;                                  // out of scope
            if (!CrawlPolicy.IsIndexableExtension(normalized)) return null;
            return normalized;
        }

        return null;
    }

    private void EnqueueSingle(CrawlContext ctx, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (!ctx.AllowedHosts.Contains(uri.Host)) return;
        var robots = ctx.RobotsCache.TryGetValue(uri.Host, out var r) ? r : RobotsRules.AllowAll;
        if (!IsAllowedByRobots(url, robots)) return;
        if (ctx.Visited.Add(url)) ctx.Queue.Enqueue(url);
    }

    private async Task EnqueueStoredOutlinksAsync(CrawlContext ctx, string url, CancellationToken cancellationToken)
    {
        foreach (var link in await GetStoredOutlinksAsync(url, cancellationToken))
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) continue;
            if (!ctx.AllowedHosts.Contains(uri.Host)) continue;
            if (!CrawlPolicy.IsIndexableExtension(link)) continue;
            var robots = ctx.RobotsCache.TryGetValue(uri.Host, out var r) ? r : RobotsRules.AllowAll;
            if (!IsAllowedByRobots(link, robots)) continue;
            if (ctx.Visited.Add(link)) ctx.Queue.Enqueue(link);
        }
    }

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

    private async Task<(string? ETag, string? LastModified, string? ContentHash)> GetCrawlStateAsync(string url, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ETag, LastModified, ContentHash FROM CrawlState WHERE Url = @Url";
        cmd.Parameters.AddWithValue("@Url", url);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        return (null, null, null);
    }

    private static string ComputeHash(byte[] body) => Convert.ToHexString(SHA256.HashData(body));

    private static bool IsHtmlContentType(string? mediaType) =>
        mediaType is null || mediaType == "text/html" || mediaType == "application/xhtml+xml";

    private static string ExtractPdfText(byte[] body)
    {
        using var stream = new MemoryStream(body);
        using var pdfReader = new iText.Kernel.Pdf.PdfReader(stream);
        using var pdfDoc = new iText.Kernel.Pdf.PdfDocument(pdfReader);
        var sb = new StringBuilder();
        for (int i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
        {
            var page = pdfDoc.GetPage(i);
            sb.Append(iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor.GetTextFromPage(page, new iText.Kernel.Pdf.Canvas.Parser.Listener.SimpleTextExtractionStrategy()));
            sb.Append(' ');
        }
        return CollapseWhitespace(sb.ToString());
    }

    private static string ExtractDocxText(byte[] body)
    {
        using var stream = new MemoryStream(body);
        var doc = new NPOI.XWPF.UserModel.XWPFDocument(stream);
        var extractor = new NPOI.XWPF.Extractor.XWPFWordExtractor(doc);
        return CollapseWhitespace(extractor.Text);
    }

    private static string? ExtractTitle(HtmlDocument doc)
    {
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode is null || string.IsNullOrWhiteSpace(titleNode.InnerText)) return null;
        var title = CollapseWhitespace(HtmlEntity.DeEntitize(titleNode.InnerText));
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static string ExtractHeadings(HtmlDocument doc)
    {
        var headingTexts = new List<string>();
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode != null && !string.IsNullOrWhiteSpace(titleNode.InnerText))
        {
            headingTexts.Add(HtmlEntity.DeEntitize(titleNode.InnerText).Trim());
        }

        var hNodes = doc.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6");
        if (hNodes != null)
        {
            foreach (var node in hNodes)
            {
                if (!string.IsNullOrWhiteSpace(node.InnerText))
                {
                    headingTexts.Add(HtmlEntity.DeEntitize(node.InnerText).Trim());
                }
            }
        }

        return CollapseWhitespace(string.Join(" ", headingTexts));
    }

    /// <summary>
    /// Walks the text nodes and joins them with spaces so that adjacent block
    /// elements don't fuse ("End.Next"), decoding HTML entities along the way.
    /// </summary>
    private static string ExtractVisibleText(HtmlNode root)
    {
        var sb = new StringBuilder();
        foreach (var node in root.DescendantsAndSelf())
        {
            if (node.NodeType != HtmlNodeType.Text) continue;
            var decoded = HtmlEntity.DeEntitize(node.InnerText);
            if (string.IsNullOrWhiteSpace(decoded)) continue;
            sb.Append(decoded.Trim()).Append(' ');
        }
        return CollapseWhitespace(sb.ToString());
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(" ", text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries));

    private static string DecodeHtml(byte[] bytes, string? charset)
    {
        var encoding = ResolveEncoding(charset) ?? ResolveEncoding(SniffCharset(bytes));
        return (encoding ?? Encoding.UTF8).GetString(bytes);
    }

    private static Encoding? ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return null;
        try
        {
            return Encoding.GetEncoding(charset.Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return null; // Unknown label — let the caller fall back.
        }
    }

    /// <summary>
    /// Looks for a <c>&lt;meta charset&gt;</c> declaration in the document head when the HTTP
    /// response didn't specify one, so legacy pages aren't decoded as the wrong encoding.
    /// </summary>
    private static string? SniffCharset(byte[] bytes)
    {
        int len = Math.Min(bytes.Length, 4096);
        // Latin-1 round-trips every byte to a char, enough to read ASCII meta tags.
        var head = Encoding.Latin1.GetString(bytes, 0, len);
        int headEnd = head.IndexOf("</head", StringComparison.OrdinalIgnoreCase);
        if (headEnd >= 0) head = head[..headEnd];

        var match = CharsetRegex.Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes) return null;
        }
        return buffer.ToArray();
    }

    private static TimeSpan ResolveRequestDelay(RobotsRules robots)
    {
        double ms = DefaultRequestDelayMs;
        if (robots.CrawlDelaySeconds is double seconds && seconds > 0)
        {
            ms = Math.Max(ms, seconds * 1000);
        }
        return TimeSpan.FromMilliseconds(ms);
    }

    private async Task OptimizeDatabaseAsync()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // PRAGMA optimize is cheap; run it every time.
            using (var optimize = connection.CreateCommand())
            {
                optimize.CommandText = "PRAGMA optimize;";
                await optimize.ExecuteNonQueryAsync();
            }

            // VACUUM rewrites the whole file, so only do it when fragmentation is meaningful
            // (lots of free pages, e.g. after deleting many gone/replaced pages).
            long freelist = await ReadPragmaLongAsync(connection, "PRAGMA freelist_count;");
            long pageCount = await ReadPragmaLongAsync(connection, "PRAGMA page_count;");
            if (pageCount > 1000 && freelist > pageCount / 4)
            {
                _logger.LogInformation("Vacuuming database ({Free}/{Total} pages free)...", freelist, pageCount);
                using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM;";
                await vacuum.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to optimize database.");
        }
    }

    private static async Task<long> ReadPragmaLongAsync(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        var value = await command.ExecuteScalarAsync();
        return value is long l ? l : 0L;
    }

    private async Task<RobotsRules> GetRobotsRulesAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            var robotsUrl = new Uri(baseUri, "/robots.txt");
            using var response = await _httpClient.GetAsync(robotsUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return RobotsRules.Parse(content, UserAgent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch or parse robots.txt");
        }
        return RobotsRules.AllowAll;
    }

    private static bool IsAllowedByRobots(string url, RobotsRules robots)
    {
        return !Uri.TryCreate(url, UriKind.Absolute, out var uri) || robots.IsAllowed(uri.PathAndQuery);
    }

    private async Task EnqueueSitemapUrlsAsync(Uri hostUri, CrawlContext ctx, CancellationToken cancellationToken)
    {
        var robots = ctx.RobotsCache.TryGetValue(hostUri.Host, out var r) ? r : RobotsRules.AllowAll;

        // Seed from robots.txt Sitemap: directives plus the conventional location.
        var pending = new Queue<string>();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sitemap in robots.Sitemaps) pending.Enqueue(sitemap);
        pending.Enqueue(new Uri(hostUri, "/sitemap.xml").ToString());

        int added = 0;
        int safety = 0;
        while (pending.Count > 0 && safety++ < 200)
        {
            var sitemapUrl = pending.Dequeue();
            if (!processed.Add(sitemapUrl)) continue;

            var (locations, nestedSitemaps) = await FetchSitemapAsync(sitemapUrl, cancellationToken);

            foreach (var nested in nestedSitemaps)
            {
                if (!processed.Contains(nested)) pending.Enqueue(nested);
            }

            foreach (var loc in locations)
            {
                if (!UrlNormalizer.TryNormalize(loc, out var normalizedUrl)) continue;
                if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var locUri)) continue;
                if (!ctx.AllowedHosts.Contains(locUri.Host)) continue;
                if (!CrawlPolicy.IsIndexableExtension(normalizedUrl)) continue;

                // Check each entry against ITS OWN host's robots, not the sitemap's host.
                var locRobots = ctx.RobotsCache.TryGetValue(locUri.Host, out var lr) ? lr : RobotsRules.AllowAll;
                if (!IsAllowedByRobots(normalizedUrl, locRobots)) continue;

                if (ctx.Visited.Add(normalizedUrl))
                {
                    ctx.Queue.Enqueue(normalizedUrl);
                    added++;
                }
            }
        }

        if (added > 0)
        {
            _logger.LogInformation("Enqueued {Count} URLs from sitemaps for {Host}", added, hostUri.Host);
        }
    }

    /// <summary>
    /// Fetches one sitemap (handling .gz), returning page <c>&lt;loc&gt;</c>s from a
    /// &lt;urlset&gt; and nested sitemap <c>&lt;loc&gt;</c>s from a &lt;sitemapindex&gt;.
    /// </summary>
    private async Task<(List<string> Locations, List<string> NestedSitemaps)> FetchSitemapAsync(string sitemapUrl, CancellationToken cancellationToken)
    {
        var locations = new List<string>();
        var nested = new List<string>();
        try
        {
            using var response = await _httpClient.GetAsync(sitemapUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return (locations, nested);

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            bool gzip = sitemapUrl.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                || (response.Content.Headers.ContentEncoding?.Contains("gzip") ?? false)
                || (response.Content.Headers.ContentType?.MediaType?.Contains("gzip") ?? false);

            using var raw = new MemoryStream(bytes);
            Stream xmlStream = gzip ? new GZipStream(raw, CompressionMode.Decompress) : raw;

            var doc = new XmlDocument();
            using (xmlStream)
            {
                doc.Load(xmlStream);
            }

            bool isIndex = string.Equals(doc.DocumentElement?.LocalName, "sitemapindex", StringComparison.OrdinalIgnoreCase);
            foreach (XmlNode node in doc.GetElementsByTagName("loc"))
            {
                var value = node.InnerText?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                if (isIndex) nested.Add(value); else locations.Add(value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch or parse sitemap {Url} (it may not exist)", sitemapUrl);
        }

        return (locations, nested);
    }

    private async Task RecordCrawlStateAsync(string url, int statusCode, string? eTag, string? lastModified, string? title, string? contentHash, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO CrawlState (Url, LastCrawled, StatusCode, ETag, LastModified, Title, ContentHash)
            VALUES (@Url, @LastCrawled, @StatusCode, @ETag, @LastModified, @Title, @ContentHash)
            ON CONFLICT(Url) DO UPDATE SET
                LastCrawled = excluded.LastCrawled,
                StatusCode = excluded.StatusCode,
                ETag = excluded.ETag,
                LastModified = excluded.LastModified,
                Title = excluded.Title,
                ContentHash = excluded.ContentHash;";

        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@LastCrawled", DateTime.UtcNow);
        command.Parameters.AddWithValue("@StatusCode", statusCode);
        command.Parameters.AddWithValue("@ETag", eTag ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@LastModified", lastModified ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Title", title ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ContentHash", contentHash ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Records that a URL was visited (status + timestamp) without disturbing the
    /// stored validators, title, or content hash. Used for 304s, skips, and soft failures.</summary>
    private async Task RecordVisitAsync(string url, int statusCode, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO CrawlState (Url, LastCrawled, StatusCode)
            VALUES (@Url, @LastCrawled, @StatusCode)
            ON CONFLICT(Url) DO UPDATE SET
                LastCrawled = excluded.LastCrawled,
                StatusCode = excluded.StatusCode;";
        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@LastCrawled", DateTime.UtcNow);
        command.Parameters.AddWithValue("@StatusCode", statusCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task StoreOutlinksAsync(string fromUrl, IReadOnlyCollection<string> outlinks, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM CrawlLinks WHERE FromUrl = @From";
            delete.Parameters.AddWithValue("@From", fromUrl);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        if (outlinks.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO CrawlLinks (FromUrl, ToUrl) VALUES (@From, @To)";
            var fromParam = insert.Parameters.Add("@From", SqliteType.Text);
            var toParam = insert.Parameters.Add("@To", SqliteType.Text);
            fromParam.Value = fromUrl;
            foreach (var to in outlinks)
            {
                toParam.Value = to;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task DeleteOutlinksAsync(string fromUrl, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CrawlLinks WHERE FromUrl = @From";
        command.Parameters.AddWithValue("@From", fromUrl);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<string>> GetStoredOutlinksAsync(string url, CancellationToken cancellationToken)
    {
        var links = new List<string>();
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ToUrl FROM CrawlLinks WHERE FromUrl = @From";
        command.Parameters.AddWithValue("@From", url);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(reader.GetString(0));
        }
        return links;
    }

    private async Task RestoreFrontierAsync(CrawlContext ctx, CancellationToken cancellationToken)
    {
        var resumed = new List<string>();
        using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT Url FROM CrawlFrontier ORDER BY Seq";
            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                resumed.Add(reader.GetString(0));
            }
        }

        if (resumed.Count == 0) return;

        foreach (var url in resumed)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            if (!ctx.AllowedHosts.Contains(uri.Host)) continue;
            var robots = ctx.RobotsCache.TryGetValue(uri.Host, out var r) ? r : RobotsRules.AllowAll;
            if (!IsAllowedByRobots(url, robots)) continue;
            if (ctx.Visited.Add(url)) ctx.Queue.Enqueue(url);
        }

        // We've taken ownership of the snapshot; clear it (a fresh one is written at the end).
        using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            using var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM CrawlFrontier";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation("Resumed {Count} URLs from a previously interrupted crawl.", ctx.Queue.Count);
    }

    private async Task PersistFrontierAsync(CrawlContext ctx, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM CrawlFrontier";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        if (ctx.Queue.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO CrawlFrontier (Url, Seq) VALUES (@Url, @Seq)";
            var urlParam = insert.Parameters.Add("@Url", SqliteType.Text);
            var seqParam = insert.Parameters.Add("@Seq", SqliteType.Integer);
            int seq = 0;
            foreach (var url in ctx.Queue)
            {
                urlParam.Value = url;
                seqParam.Value = seq++;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            _logger.LogInformation("Saved {Count} pending URLs to resume next run.", ctx.Queue.Count);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private sealed class CrawlContext
    {
        public required HashSet<string> AllowedHosts;
        public required Dictionary<string, RobotsRules> RobotsCache;
        public required Queue<string> Queue;
        public required HashSet<string> Visited;
        public Dictionary<string, DateTime> LastFetchUtc = new();
    }
}
