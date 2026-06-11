using HtmlAgilityPack;
using Microsoft.Data.Sqlite;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LocalSearchEngine.Core;

public class CrawlerService
{
    /// <summary>Identifies this crawler in request headers and for robots.txt matching.</summary>
    public const string UserAgent = "LocalSearchEngine-Bot/1.0";

    /// <summary>Hard cap on a single response body; larger documents are skipped.</summary>
    private const long MaxDownloadBytes = 25L * 1024 * 1024;

    /// <summary>Minimum politeness gap between requests to the same host.</summary>
    private const int DefaultRequestDelayMs = 250;

    private static readonly char[] WordSeparators = { ' ', '\n', '\r', '\t' };

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

        using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS CrawlState (
                Url TEXT PRIMARY KEY,
                LastCrawled DATETIME,
                StatusCode INTEGER,
                ETag TEXT,
                LastModified TEXT
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS text_chunks_fts USING fts5(Id UNINDEXED, Url UNINDEXED, Text);

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

    public async Task CrawlAsync(string seedUrl, int maxPages = int.MaxValue, IEnumerable<string>? allowedServers = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(seedUrl, UriKind.Absolute, out var baseUri))
        {
            _logger.LogError("Invalid seed URL: {Url}", seedUrl);
            return;
        }

        var normalizedSeed = UrlNormalizer.Normalize(baseUri);
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allowedServers != null)
        {
            foreach (var s in allowedServers) allowedHosts.Add(s);
        }
        allowedHosts.Add(baseUri.Host);

        var robotsCache = new Dictionary<string, RobotsRules>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in allowedHosts)
        {
            var hostUri = new Uri($"{baseUri.Scheme}://{host}");
            var rules = await GetRobotsRulesAsync(hostUri, cancellationToken);
            robotsCache[host] = rules;
            await EnqueueSitemapUrlsAsync(hostUri, rules, allowedHosts, queue, visited, cancellationToken);
        }

        int pagesCrawled = 0;

        visited.Add(normalizedSeed);
        if (IsAllowedByRobots(normalizedSeed, robotsCache[baseUri.Host]))
        {
            queue.Enqueue(normalizedSeed);
        }
        else
        {
            _logger.LogWarning("Seed URL is disallowed by robots.txt: {Url}", normalizedSeed);
        }

        while (queue.Count > 0 && pagesCrawled < maxPages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Crawl cancelled after {PagesCrawled} pages.", pagesCrawled);
                break;
            }

            var currentUrl = queue.Dequeue();
            pagesCrawled++;
            _logger.LogInformation("Crawling ({PagesCrawled} / {TotalDiscovered}): {Url}", pagesCrawled, visited.Count, currentUrl);

            try
            {
                if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri)) continue;
                var currentHost = currentUri.Host;
                var currentRobots = robotsCache.TryGetValue(currentHost, out var r) ? r : RobotsRules.AllowAll;
                var requestDelay = ResolveRequestDelay(currentRobots);

                // Be a polite citizen: pause between requests to the same host.
                await Task.Delay(requestDelay, cancellationToken);

                (string? existingETag, string? existingLastModified) = await GetCachedValidatorsAsync(currentUrl, cancellationToken);

                var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                if (!string.IsNullOrEmpty(existingETag))
                {
                    request.Headers.IfNoneMatch.ParseAdd(existingETag);
                }
                if (!string.IsNullOrEmpty(existingLastModified) && DateTimeOffset.TryParse(existingLastModified, out var lastModDate))
                {
                    request.Headers.IfModifiedSince = lastModDate;
                }

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var statusCode = (int)response.StatusCode;

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    _logger.LogInformation("Page not modified since last crawl (304): {Url}", currentUrl);
                    continue;
                }

                var finalUrl = response.RequestMessage?.RequestUri?.ToString();
                if (finalUrl != null && !string.Equals(finalUrl, currentUrl, StringComparison.OrdinalIgnoreCase))
                {
                    var normalizedFinalUrl = UrlNormalizer.TryNormalize(finalUrl, out var nf) ? nf : finalUrl;
                    if (!string.Equals(normalizedFinalUrl, currentUrl, StringComparison.OrdinalIgnoreCase) && visited.Contains(normalizedFinalUrl))
                    {
                        _logger.LogInformation("Redirected to already visited URL: {Url}", normalizedFinalUrl);
                        continue;
                    }
                    visited.Add(normalizedFinalUrl);
                    currentUrl = normalizedFinalUrl;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to crawl {Url} with status code {StatusCode}", currentUrl, statusCode);
                    await RecordCrawlStateAsync(currentUrl, statusCode, null, null, cancellationToken);
                    await _vectorSearchService.DeleteUrlChunksAsync(currentUrl);
                    continue;
                }

                if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                {
                    _logger.LogWarning("Skipping {Url}: content length {Length} exceeds limit {Limit}", currentUrl, response.Content.Headers.ContentLength, MaxDownloadBytes);
                    continue;
                }

                var body = await ReadBoundedAsync(response.Content, MaxDownloadBytes, cancellationToken);
                if (body is null)
                {
                    _logger.LogWarning("Skipping {Url}: response body exceeds limit {Limit}", currentUrl, MaxDownloadBytes);
                    continue;
                }

                string? newETag = response.Headers.ETag?.Tag;
                string? newLastModified = response.Content.Headers.LastModified?.ToString("r");

                var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                var extension = Path.GetExtension((finalUrl ?? currentUrl).Split('?')[0])?.ToLowerInvariant();

                string cleanedText;
                string joinedHeadings = string.Empty;
                int addedLinks = 0;

                if ((contentType != null && contentType.Contains("pdf")) || extension == ".pdf")
                {
                    cleanedText = ExtractPdfText(body);
                }
                else if ((contentType != null && contentType.Contains("wordprocessingml")) || extension == ".docx")
                {
                    cleanedText = ExtractDocxText(body);
                }
                else
                {
                    var html = DecodeHtml(body, response.Content.Headers.ContentType?.CharSet);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    joinedHeadings = ExtractHeadings(doc);

                    // Remove non-content nodes before harvesting body text.
                    var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header|//svg");
                    if (nodesToRemove != null)
                    {
                        foreach (var node in nodesToRemove) node.Remove();
                    }

                    cleanedText = ExtractVisibleText(doc.DocumentNode);
                    addedLinks = EnqueueLinks(doc, currentUrl, allowedHosts, robotsCache, queue, visited);
                }

                // Replace any previously indexed chunks for this URL, then re-index.
                await _vectorSearchService.DeleteUrlChunksAsync(currentUrl);
                await _vectorSearchService.IndexUrlChunksAsync(currentUrl, cleanedText, isHeading: false);

                if (!string.IsNullOrWhiteSpace(joinedHeadings))
                {
                    await _vectorSearchService.IndexUrlChunksAsync(currentUrl, joinedHeadings, isHeading: true);
                }

                await RecordCrawlStateAsync(currentUrl, statusCode, newETag, newLastModified, cancellationToken);
                _logger.LogInformation("Indexed {Url} and queued {AddedLinks} new internal links.", currentUrl, addedLinks);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Crawl cancelled while processing {Url}.", currentUrl);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while crawling {Url}", currentUrl);
                await RecordCrawlStateAsync(currentUrl, 500, null, null, CancellationToken.None);
                await _vectorSearchService.DeleteUrlChunksAsync(currentUrl);
            }
        }

        _logger.LogInformation("Crawling completed for {SeedUrl}", seedUrl);
        await OptimizeDatabaseAsync();
    }

    private int EnqueueLinks(HtmlDocument doc, string currentUrl, HashSet<string> allowedHosts, Dictionary<string, RobotsRules> robotsCache, Queue<string> queue, HashSet<string> visited)
    {
        var linkNodes = doc.DocumentNode.SelectNodes("//a[@href]");
        if (linkNodes is null) return 0;

        var baseForLinks = new Uri(currentUrl);
        int addedLinks = 0;

        foreach (var link in linkNodes)
        {
            var href = link.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(baseForLinks, href, out var absoluteUri)) continue;

            var normalizedUrl = UrlNormalizer.Normalize(absoluteUri);
            var linkHost = absoluteUri.Host;

            if (!allowedHosts.Contains(linkHost) || !CrawlPolicy.IsIndexableExtension(normalizedUrl))
            {
                _logger.LogTrace("Skipped link (external or invalid extension): {Url}", normalizedUrl);
                continue;
            }

            if (!visited.Add(normalizedUrl)) continue;

            var linkRobots = robotsCache.TryGetValue(linkHost, out var r) ? r : RobotsRules.AllowAll;

            if (IsAllowedByRobots(normalizedUrl, linkRobots))
            {
                queue.Enqueue(normalizedUrl);
                addedLinks++;
                _logger.LogDebug("Discovered new link: {Url}", normalizedUrl);
            }
            else
            {
                _logger.LogTrace("Skipped link (disallowed by robots.txt): {Url}", normalizedUrl);
            }
        }

        return addedLinks;
    }

    private async Task<(string? ETag, string? LastModified)> GetCachedValidatorsAsync(string url, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ETag, LastModified FROM CrawlState WHERE Url = @Url";
        cmd.Parameters.AddWithValue("@Url", url);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            string? etag = reader.IsDBNull(0) ? null : reader.GetString(0);
            string? lastModified = reader.IsDBNull(1) ? null : reader.GetString(1);
            return (etag, lastModified);
        }
        return (null, null);
    }

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
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"', '\'')).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // Unknown charset label — fall back to UTF-8.
            }
        }
        return Encoding.UTF8.GetString(bytes);
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

    private TimeSpan ResolveRequestDelay(RobotsRules robots)
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
            _logger.LogInformation("Optimizing and vacuuming database...");
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA optimize; VACUUM;";
            await command.ExecuteNonQueryAsync();
            _logger.LogInformation("Database optimization complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to optimize database.");
        }
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

    private async Task EnqueueSitemapUrlsAsync(Uri hostUri, RobotsRules robots, HashSet<string> allowedHosts, Queue<string> queue, HashSet<string> visited, CancellationToken cancellationToken)
    {
        try
        {
            var sitemapUrl = new Uri(hostUri, "/sitemap.xml");
            using var response = await _httpClient.GetAsync(sitemapUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(content);
            var locNodes = doc.GetElementsByTagName("loc");
            int addedFromSitemap = 0;

            foreach (System.Xml.XmlNode node in locNodes)
            {
                if (!UrlNormalizer.TryNormalize(node.InnerText?.Trim(), out var normalizedUrl)) continue;
                if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var locUri)) continue;

                if (allowedHosts.Contains(locUri.Host) &&
                    CrawlPolicy.IsIndexableExtension(normalizedUrl) &&
                    visited.Add(normalizedUrl) &&
                    IsAllowedByRobots(normalizedUrl, robots))
                {
                    queue.Enqueue(normalizedUrl);
                    addedFromSitemap++;
                }
            }

            _logger.LogInformation("Enqueued {Count} URLs from sitemap.xml for {Host}", addedFromSitemap, hostUri.Host);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch or parse sitemap.xml for {Host} (it may not exist)", hostUri.Host);
        }
    }

    private async Task RecordCrawlStateAsync(string url, int statusCode, string? eTag, string? lastModified, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO CrawlState (Url, LastCrawled, StatusCode, ETag, LastModified)
            VALUES (@Url, @LastCrawled, @StatusCode, @ETag, @LastModified)
            ON CONFLICT(Url) DO UPDATE SET
                LastCrawled = excluded.LastCrawled,
                StatusCode = excluded.StatusCode,
                ETag = excluded.ETag,
                LastModified = excluded.LastModified;";

        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@LastCrawled", DateTime.UtcNow);
        command.Parameters.AddWithValue("@StatusCode", statusCode);
        command.Parameters.AddWithValue("@ETag", eTag ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@LastModified", lastModified ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
