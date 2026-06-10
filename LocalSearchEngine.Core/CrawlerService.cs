using HtmlAgilityPack;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LocalSearchEngine.Core;

public class CrawlerService
{
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

    public async Task InitializeAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS CrawlState (
                Url TEXT PRIMARY KEY,
                LastCrawled DATETIME,
                StatusCode INTEGER,
                ExtractedText TEXT,
                ETag TEXT,
                LastModified TEXT
            )";
        await command.ExecuteNonQueryAsync();
    }

    public async Task CrawlAsync(string seedUrl, int maxPages = int.MaxValue)
    {
        if (!Uri.TryCreate(seedUrl, UriKind.Absolute, out var baseUri))
        {
            _logger.LogError("Invalid seed URL: {Url}", seedUrl);
            return;
        }

        var seedBuilder = new UriBuilder(baseUri);
        seedBuilder.Fragment = string.Empty;
        if (seedBuilder.Path.Length > 1 && seedBuilder.Path.EndsWith("/"))
        {
            seedBuilder.Path = seedBuilder.Path.TrimEnd('/');
        }
        var normalizedSeed = seedBuilder.Uri.ToString();

        var host = baseUri.Host;
        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        var disallowedPaths = await GetDisallowedPathsAsync(baseUri);

        int pagesCrawled = 0;

        visited.Add(normalizedSeed);
        if (IsAllowedByRobots(normalizedSeed, disallowedPaths))
        {
            queue.Enqueue(normalizedSeed);
        }
        else
        {
            _logger.LogWarning("Seed URL is disallowed by robots.txt: {Url}", normalizedSeed);
        }

        while (queue.Count > 0 && pagesCrawled < maxPages)
        {
            var currentUrl = queue.Dequeue();
            pagesCrawled++;
            _logger.LogInformation("Crawling ({PagesCrawled} / {TotalDiscovered}): {Url}", pagesCrawled, visited.Count, currentUrl);

            try
            {
                string? existingETag = null;
                string? existingLastModified = null;
                
                using (var connection = new SqliteConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT ETag, LastModified FROM CrawlState WHERE Url = @Url";
                    cmd.Parameters.AddWithValue("@Url", currentUrl);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        // Check if column exists, since it might have been added
                        // By checking reader field count we avoid errors if the try/catch in Init failed
                        if (reader.FieldCount >= 2) 
                        {
                            existingETag = reader.IsDBNull(0) ? null : reader.GetString(0);
                            existingLastModified = reader.IsDBNull(1) ? null : reader.GetString(1);
                        }
                    }
                }

                var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                if (!string.IsNullOrEmpty(existingETag))
                {
                    request.Headers.IfNoneMatch.ParseAdd(existingETag);
                }
                if (!string.IsNullOrEmpty(existingLastModified))
                {
                    if (DateTimeOffset.TryParse(existingLastModified, out var lastModDate))
                    {
                        request.Headers.IfModifiedSince = lastModDate;
                    }
                }

                var response = await _httpClient.SendAsync(request);
                var statusCode = (int)response.StatusCode;

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    _logger.LogInformation("Page not modified since last crawl (304): {Url}", currentUrl);
                    continue;
                }

                var finalUrl = response.RequestMessage?.RequestUri?.ToString();
                if (finalUrl != null && !string.Equals(finalUrl, currentUrl, StringComparison.OrdinalIgnoreCase))
                {
                    var builder = new UriBuilder(finalUrl);
                    builder.Fragment = string.Empty;
                    if (builder.Path.Length > 1 && builder.Path.EndsWith("/"))
                    {
                        builder.Path = builder.Path.TrimEnd('/');
                    }
                    var normalizedFinalUrl = builder.Uri.ToString();
                    
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
                    await RecordCrawlStateAsync(currentUrl, statusCode, null, null, null);
                    await _vectorSearchService.DeleteUrlChunksAsync(currentUrl);
                    continue;
                }

                string? newETag = response.Headers.ETag?.Tag;
                string? newLastModified = response.Content.Headers.LastModified?.ToString("r");

                var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                var extension = Path.GetExtension(finalUrl ?? currentUrl)?.ToLowerInvariant();

                string cleanedText = string.Empty;
                string joinedHeadings = string.Empty;
                int addedLinks = 0;

                if ((contentType != null && contentType.Contains("pdf")) || extension == ".pdf")
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var pdfReader = new iText.Kernel.Pdf.PdfReader(stream);
                    using var pdfDoc = new iText.Kernel.Pdf.PdfDocument(pdfReader);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 1; i <= pdfDoc.GetNumberOfPages(); i++)
                    {
                        var page = pdfDoc.GetPage(i);
                        sb.Append(iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor.GetTextFromPage(page, new iText.Kernel.Pdf.Canvas.Parser.Listener.SimpleTextExtractionStrategy()));
                        sb.Append(" ");
                    }
                    cleanedText = string.Join(" ", sb.ToString().Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                }
                else if ((contentType != null && contentType.Contains("wordprocessingml")) || extension == ".docx")
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    var doc = new NPOI.XWPF.UserModel.XWPFDocument(stream);
                    var extractor = new NPOI.XWPF.Extractor.XWPFWordExtractor(doc);
                    cleanedText = string.Join(" ", extractor.Text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                }
                else
                {
                    // HTML parsing
                    var html = await response.Content.ReadAsStringAsync();
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var headingTexts = new List<string>();
                    var titleNode = doc.DocumentNode.SelectSingleNode("//title");
                    if (titleNode != null && !string.IsNullOrWhiteSpace(titleNode.InnerText)) headingTexts.Add(titleNode.InnerText.Trim());
                    var hNodes = doc.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6");
                    if (hNodes != null)
                    {
                        foreach (var node in hNodes)
                        {
                            if (!string.IsNullOrWhiteSpace(node.InnerText)) headingTexts.Add(node.InnerText.Trim());
                        }
                    }
                    joinedHeadings = string.Join(" ", headingTexts);

                    // Remove non-content nodes
                    var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header|//svg");
                    if (nodesToRemove != null)
                    {
                        foreach (var node in nodesToRemove)
                        {
                            node.Remove();
                        }
                    }

                    var text = doc.DocumentNode.InnerText;
                    cleanedText = string.Join(" ", text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));

                    // Extract and enqueue links
                    var linkNodes = doc.DocumentNode.SelectNodes("//a[@href]");
                    
                    if (linkNodes != null)
                    {
                        foreach (var link in linkNodes)
                        {
                            var href = link.GetAttributeValue("href", string.Empty);
                            if (string.IsNullOrWhiteSpace(href)) continue;

                            if (Uri.TryCreate(new Uri(currentUrl), href, out var absoluteUri))
                            {
                                // Normalize URL (remove fragments but keep query string)
                                var builder = new UriBuilder(absoluteUri);
                                builder.Fragment = string.Empty;
                                if (builder.Path.Length > 1 && builder.Path.EndsWith("/"))
                                {
                                    builder.Path = builder.Path.TrimEnd('/');
                                }
                                var normalizedUrl = builder.Uri.ToString();

                                // Check domain and extensions
                                if (absoluteUri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
                                    IsValidExtension(normalizedUrl))
                                {
                                    if (!visited.Contains(normalizedUrl))
                                    {
                                        visited.Add(normalizedUrl);
                                        if (IsAllowedByRobots(normalizedUrl, disallowedPaths))
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
                                }
                                else
                                {
                                    _logger.LogTrace("Skipped link (external or invalid extension): {Url}", normalizedUrl);
                                }
                            }
                        }
                    }
                }

                // Clean old chunks and re-index
                await _vectorSearchService.DeleteUrlChunksAsync(currentUrl);
                await _vectorSearchService.IndexUrlChunksAsync(currentUrl, cleanedText, isHeading: false);
                
                if (!string.IsNullOrWhiteSpace(joinedHeadings))
                {
                    await _vectorSearchService.IndexUrlChunksAsync(currentUrl, joinedHeadings, isHeading: true);
                }

                // Record success
                await RecordCrawlStateAsync(currentUrl, statusCode, cleanedText, newETag, newLastModified);
                
                _logger.LogInformation("Indexed {Url} and queued {AddedLinks} new internal links.", currentUrl, addedLinks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while crawling {Url}", currentUrl);
                await RecordCrawlStateAsync(currentUrl, 500, null, null, null);
                await _vectorSearchService.DeleteUrlChunksAsync(currentUrl);
            }
        }
        
        _logger.LogInformation("Crawling completed for {SeedUrl}", seedUrl);
        await OptimizeDatabaseAsync();
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

    private bool IsValidExtension(string url)
    {
        var ext = Path.GetExtension(url)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return true; // No extension is usually an HTML page route
        
        var validExtensions = new[] { ".html", ".htm", ".pdf", ".docx" };
        return validExtensions.Contains(ext);
    }

    private async Task<List<string>> GetDisallowedPathsAsync(Uri baseUri)
    {
        var disallowed = new List<string>();
        try
        {
            var robotsUrl = new Uri(baseUri, "/robots.txt");
            var response = await _httpClient.GetAsync(robotsUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                bool isWildcardAgent = true; // Assume true initially in case no user-agent is specified
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("#")) continue;
                    
                    if (trimmed.StartsWith("User-agent:", StringComparison.OrdinalIgnoreCase))
                    {
                        var agent = trimmed.Substring("User-agent:".Length).Trim();
                        isWildcardAgent = agent == "*";
                    }
                    else if (isWildcardAgent && trimmed.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = trimmed.Substring("Disallow:".Length).Trim();
                        if (!string.IsNullOrEmpty(path))
                        {
                            disallowed.Add(path);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch or parse robots.txt");
        }
        return disallowed;
    }

    private bool IsAllowedByRobots(string url, List<string> disallowedPaths)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var pathAndQuery = uri.PathAndQuery;
            foreach (var disallowed in disallowedPaths)
            {
                if (pathAndQuery.StartsWith(disallowed, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private async Task RecordCrawlStateAsync(string url, int statusCode, string? extractedText, string? eTag, string? lastModified)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO CrawlState (Url, LastCrawled, StatusCode, ExtractedText, ETag, LastModified)
            VALUES (@Url, @LastCrawled, @StatusCode, @ExtractedText, @ETag, @LastModified)
            ON CONFLICT(Url) DO UPDATE SET
                LastCrawled = excluded.LastCrawled,
                StatusCode = excluded.StatusCode,
                ExtractedText = excluded.ExtractedText,
                ETag = excluded.ETag,
                LastModified = excluded.LastModified;";
        
        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@LastCrawled", DateTime.UtcNow);
        command.Parameters.AddWithValue("@StatusCode", statusCode);
        command.Parameters.AddWithValue("@ExtractedText", extractedText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ETag", eTag ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@LastModified", lastModified ?? (object)DBNull.Value);
        
        await command.ExecuteNonQueryAsync();
    }
}
