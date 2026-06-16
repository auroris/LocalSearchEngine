using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.Crawling.Extraction;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Fetches and parses pages, checks politeness and robots rules, and produces jobs for the database writer.
/// </summary>
internal sealed class CrawlProducer
{
    private const string UserAgentToken = "localsearchengine-bot";
    private const int DefaultRequestDelayMs = 250;
    private const int MaxCrawlDelaySeconds = 30;
    private const int LinkCheckConcurrency = 8;
    private static readonly TimeSpan LinkCheckPerHostGap = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _httpClient;
    private readonly VectorSearchService _vectorSearchService;
    private readonly ChannelWriter<CrawlJob> _writer;
    private readonly CrawlContext _context;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrawlProducer"/> class.
    /// </summary>
    public CrawlProducer(
        HttpClient httpClient,
        VectorSearchService vectorSearchService,
        ChannelWriter<CrawlJob> writer,
        CrawlContext context,
        ILogger logger)
    {
        _httpClient = httpClient;
        _vectorSearchService = vectorSearchService;
        _writer = writer;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Executes the main crawl loop, fetching pages from the queue and producing crawl jobs until the queue is empty or caps are hit.
    /// </summary>
    public async Task<(int ProducedJobs, int IndexedCount)> ProduceAsync(int maxPages, int maxPagesPerHost, CancellationToken cancellationToken)
    {
        int indexedCount = 0;
        int producedJobs = 0;

        _context.Observer.OnPhaseChanged(CrawlPhase.Crawling);
        while (_context.Queue.Count > 0 && indexedCount < maxPages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _context.Observer.OnCrawlCancelled(indexedCount);
                break;
            }

            var currentUrl = _context.Queue.Dequeue();

            // Safety valve against crawler traps (calendars, faceted nav): once a host has
            // contributed its cap of indexed pages, stop fetching more of its URLs.
            if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentHostUri)
                && _context.IndexedPerHost.TryGetValue(currentHostUri.Host, out var hostIndexed)
                && hostIndexed >= maxPagesPerHost)
            {
                // A skipped URL means "not visited" no longer implies "gone", so this run
                // must not prune.
                _context.HostCapSkipped = true;
                _context.Observer.OnHostCapReached(maxPagesPerHost, currentHostUri.Host, currentUrl);
                continue;
            }

            // A host written off as unreachable earlier this run: don't spend any more requests
            // on it. Its URLs are exempt from stale pruning (see PruneStaleUrlsAsync), so skipping
            // them here never drops their existing index entries.
            if (currentHostUri is not null && _context.HostHealth.IsUnreachable(currentHostUri.Host))
            {
                continue;
            }

            _context.Observer.OnPageFetching(indexedCount, _context.Visited.Count, currentUrl);

            CrawlJob? job;
            try
            {
                job = await ProduceJobAsync(currentUrl, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _context.Observer.OnFetchCancelled(currentUrl);
                break;
            }
            catch (Exception ex)
            {
                // Fetch/parse failed unexpectedly: note the visit but KEEP any content
                // already indexed for this URL — a transient failure must not erase data.
                _context.Observer.OnFetchError(ex, currentUrl);
                job = new TouchJob(currentUrl, 500);
            }

            if (job is not null)
            {
                producedJobs++;
                await _writer.WriteAsync(job, CancellationToken.None);
                if (job is IndexJob)
                {
                    indexedCount++;
                    if (Uri.TryCreate(job.Url, UriKind.Absolute, out var indexedUri))
                    {
                        _context.IndexedPerHost.TryGetValue(indexedUri.Host, out var n);
                        _context.IndexedPerHost[indexedUri.Host] = n + 1;
                    }
                }
            }
        }

        return (producedJobs, indexedCount);
    }

    /// <summary>
    /// Returns the cached robots.txt rules for the URL's origin, fetching and caching them on
    /// first contact.
    /// </summary>
    public async Task<RobotsRules> GetOrFetchRobotsAsync(Uri uri, CancellationToken cancellationToken)
    {
        var origin = UrlOrigin.Key(uri);
        if (_context.RobotsCache.TryGetValue(origin, out var cached)) return cached;
        var (rules, unavailable, reachable) = await GetRobotsRulesAsync(UrlOrigin.BaseUri(uri), _context.MaxCrawlSizeBytes, cancellationToken);

        if (reachable)
        {
            _context.HostHealth.RecordContacted(uri.Host);
        }
        else if (_context.HostHealth.RecordUnreachable(uri.Host))
        {
            _logger.LogWarning("Host {Host} did not respond on first contact; writing it off and skipping its URLs for the rest of this run.", uri.Host);
        }

        if (unavailable)
        {
            _context.RobotsUnavailable.Add(origin);
        }
        _context.RobotsCache[origin] = rules;
        return rules;
    }

    /// <summary>
    /// Discovers sitemaps for the seed's origin and enqueues the entries that live on that origin.
    /// </summary>
    public async Task EnqueueSitemapUrlsAsync(Uri originUri, CancellationToken cancellationToken)
    {
        var originKey = UrlOrigin.Key(originUri);
        var robots = _context.RobotsCache.TryGetValue(originKey, out var r) ? r : RobotsRules.AllowAll;

        var pending = new Queue<string>();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sitemap in robots.Sitemaps) pending.Enqueue(sitemap);
        pending.Enqueue(new Uri(originUri, "/sitemap.xml").ToString());

        int added = 0;
        int safety = 0;
        while (pending.Count > 0 && safety++ < 200)
        {
            var sitemapUrl = pending.Dequeue();
            if (!processed.Add(sitemapUrl)) continue;
            if (!Uri.TryCreate(sitemapUrl, UriKind.Absolute, out var sitemapUri) || !_context.AllowedHosts.IsAllowed(sitemapUri))
            {
                _logger.LogInformation("Skipping out-of-scope sitemap: {Url}", sitemapUrl);
                continue;
            }

            var (locations, nestedSitemaps) = await FetchSitemapAsync(sitemapUrl, _context.MaxCrawlSizeBytes, cancellationToken);

            foreach (var nested in nestedSitemaps)
            {
                if (!processed.Contains(nested)) pending.Enqueue(nested);
            }

            foreach (var loc in locations)
            {
                if (!UrlNormalizer.TryNormalize(loc, out var normalizedUrl)) continue;
                if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var locUri)) continue;

                if (!string.Equals(UrlOrigin.Key(locUri), originKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (!CrawlPolicy.IsAllowedByRobots(normalizedUrl, robots)) continue;

                if (Discover(normalizedUrl))
                {
                    added++;
                }
            }
        }

        if (added > 0)
        {
            _logger.LogInformation("Enqueued {Count} URLs from sitemaps for {Origin}", added, originKey);
        }
    }

    /// <summary>
    /// Removes already-indexed URLs that the robots.txt fetched for their origin this crawl now disallows.
    /// </summary>
    public async Task<int> RemoveRobotsBannedUrlsAsync()
    {
        int removed = 0;
        try
        {
            foreach (var (origin, rules) in _context.RobotsCache)
            {
                if (_context.RobotsUnavailable.Contains(origin)) continue;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) continue;
                if (_context.HostHealth.IsUnreachable(originUri.Host)) continue;

                var candidates = await CrawlStore.GetCrawledUrlsWithPrefixAsync(
                    _context.Read, originUri.GetLeftPart(UriPartial.Authority), CancellationToken.None);
                foreach (var url in candidates)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                    if (!string.Equals(UrlOrigin.Key(uri), origin, StringComparison.OrdinalIgnoreCase)) continue;
                    if (CrawlPolicy.IsAllowedByRobots(url, rules)) continue;

                    await _vectorSearchService.DeleteUrlChunksAsync(url);
                    await CrawlStore.DeleteLinksAsync(_context.Write, url, CancellationToken.None);
                    await CrawlStore.DeleteCrawlStateAsync(_context.Write, url, CancellationToken.None);
                    removed++;
                }
            }
        }
        catch (Exception ex)
        {
            _context.Observer.OnRemoveBannedFailed(ex);
        }
        return removed;
    }

    /// <summary>
    /// Removes index entries for in-scope URLs the completed crawl never reached.
    /// </summary>
    public async Task<int> PruneStaleUrlsAsync(DateTime crawlStartUtc)
    {
        int pruned = 0;
        try
        {
            var candidates = await CrawlStore.GetUrlsNotCrawledSinceAsync(_context.Read, crawlStartUtc, CancellationToken.None);
            foreach (var url in candidates)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                if (!_context.AllowedHosts.IsAllowed(uri)) continue;
                if (_context.RobotsUnavailable.Contains(UrlOrigin.Key(uri))) continue;
                if (_context.HostHealth.IsUnreachable(uri.Host)) continue;

                await _vectorSearchService.DeleteUrlChunksAsync(url);
                await CrawlStore.DeleteLinksAsync(_context.Write, url, CancellationToken.None);
                await CrawlStore.DeleteCrawlStateAsync(_context.Write, url, CancellationToken.None);
                pruned++;
            }
        }
        catch (Exception ex)
        {
            _context.Observer.OnPruneFailed(ex);
        }
        return pruned;
    }

    /// <summary>
    /// Verify links not already determined this run — off-site links (never crawled) plus any
    /// in-scope links the crawl didn't reach.
    /// </summary>
    public async Task VerifyUndeterminedLinksAsync(DateTime crawlStartUtc, CancellationToken cancellationToken)
    {
        var rows = await CrawlStore.GetLinksToVerifyAsync(_context.Read, crawlStartUtc, CancellationToken.None);

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fromUrl, toUrl, external) in rows)
        {
            if (external && !_context.CheckExternalLinks) continue;
            if (!Uri.TryCreate(fromUrl, UriKind.Absolute, out var fromUri) || !_context.AllowedHosts.IsAllowed(fromUri)) continue;
            if (Uri.TryCreate(toUrl, UriKind.Absolute, out var toUri) && _context.HostHealth.IsUnreachable(toUri.Host)) continue;
            destinations.Add(toUrl);
        }

        if (destinations.Count == 0) return;

        _context.Observer.OnPhaseChanged(CrawlPhase.CheckingLinks);

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

                        var (status, statusCode) = await ProbeLinkAsync(target, token);
                        results.Add((target, status, statusCode));
                    }
                });
        }
        catch (OperationCanceledException)
        {
        }

        int broken = 0, redirected = 0;
        foreach (var (target, status, statusCode) in results)
        {
            await CrawlStore.UpdateLinkStatusByDestinationAsync(_context.Write, target, (int)status, statusCode, CancellationToken.None);
            if (status == LinkStatus.Error) broken++;
            else if (status == LinkStatus.Redirect) redirected++;
        }

        _context.Observer.OnLinksVerified(results.Count, broken, redirected);
    }

    /// <summary>
    /// Builds the report's broken and redirected link lists from the link index.
    /// </summary>
    public async Task<(List<BrokenLink> Broken, List<BrokenLink> Redirected)> BuildLinkReportAsync(DateTime crawlStartUtc)
    {
        var rows = await CrawlStore.GetReportableLinksAsync(_context.Read, crawlStartUtc, CancellationToken.None);
        var broken = new List<BrokenLink>();
        var redirected = new List<BrokenLink>();

        foreach (var (fromUrl, toUrl, external, statusVal, statusCode) in rows)
        {
            var status = (LinkStatus)statusVal;
            if (status == LinkStatus.Error)
            {
                broken.Add(new BrokenLink(toUrl, fromUrl, external, statusCode, ReasonFor(status, statusCode)));
            }
            else if (status == LinkStatus.Redirect)
            {
                redirected.Add(new BrokenLink(toUrl, fromUrl, external, statusCode, ReasonFor(status, statusCode)));
            }
        }

        broken.Sort(CompareLinks);
        redirected.Sort(CompareLinks);

        return (broken, redirected);
    }

    private async Task<CrawlJob?> ProduceJobAsync(string currentUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri)) return null;
        if (!_context.AllowedHosts.IsAllowed(currentUri))
        {
            _context.Observer.OnOutScopeUrlReached(currentUrl);
            return null;
        }

        var currentRobots = await GetOrFetchRobotsAsync(currentUri, cancellationToken);

        if (_context.HostHealth.IsUnreachable(currentUri.Host))
        {
            return null;
        }

        if (!CrawlPolicy.IsAllowedByRobots(currentUrl, currentRobots))
        {
            _context.Observer.OnPageDisallowed(currentUrl);
            return null;
        }
        await DelayForHostAsync(currentUri.Host, ResolveRequestDelay(currentRobots), cancellationToken);

        var state = await CrawlStore.GetCrawlStateAsync(_context.Read, currentUrl, cancellationToken);

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

        _context.HostHealth.RecordContacted(currentUri.Host);

        int statusCode = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            _context.Observer.OnPageUnchanged(currentUrl);
            await EnqueueStoredOutlinksAsync(currentUrl, cancellationToken);
            return new TouchJob(currentUrl, statusCode);
        }

        var finalUrl = currentUrl;
        string? redirectSourceUrl = null;
        var finalRequestUri = response.RequestMessage?.RequestUri;
        if (finalRequestUri != null)
        {
            var normalizedFinal = UrlNormalizer.Normalize(finalRequestUri);
            if (!string.Equals(normalizedFinal, currentUrl, StringComparison.OrdinalIgnoreCase))
            {
                redirectSourceUrl = currentUrl;

                if (!_context.AllowedHosts.IsAllowed(finalRequestUri))
                {
                    if (string.Equals(currentUrl, _context.SeedUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        _context.Observer.OnSeedRedirectedToNewOrigin(currentUrl, UrlOrigin.Key(finalRequestUri));
                        _context.AllowedHosts.AddOrigin(finalRequestUri);
                    }
                    else
                    {
                        _context.Observer.OnPageRedirectedOutScope(currentUrl, normalizedFinal);
                        return new AliasJob(currentUrl, 302);
                    }
                }
                var finalRobots = await GetOrFetchRobotsAsync(finalRequestUri, cancellationToken);
                if (!CrawlPolicy.IsAllowedByRobots(normalizedFinal, finalRobots))
                {
                    _context.Observer.OnPageRedirectedDisallowed(currentUrl, normalizedFinal);
                    return new AliasJob(currentUrl, 302);
                }
                if (!_context.Visited.Add(normalizedFinal))
                {
                    _context.Observer.OnPageRedirectedAlreadySeen(currentUrl, normalizedFinal);
                    return new AliasJob(currentUrl, 302);
                }
                finalUrl = normalizedFinal;
            }
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            _context.Observer.OnPageGone(currentUrl, finalUrl, statusCode);
            return new GoneJob(finalUrl, statusCode, redirectSourceUrl);
        }

        if (!response.IsSuccessStatusCode)
        {
            _context.Observer.OnPageFailed(currentUrl, finalUrl, statusCode);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > _context.MaxCrawlSizeBytes)
        {
            _context.Observer.OnPageSkippedSize(currentUrl, finalUrl, contentLength.Value, _context.MaxCrawlSizeBytes);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!CrawlPolicy.IsSupportedOrGenericContentType(contentType))
        {
            _context.Observer.OnPageSkippedType(currentUrl, finalUrl, contentType);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        byte[] body;
        using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        using (var bodyStream = new MemoryStream())
        {
            byte[] buffer = new byte[8192];
            int bytesRead;
            bool checkedPrefix = false;

            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                if (bodyStream.Length + bytesRead > _context.MaxCrawlSizeBytes)
                {
                    _context.Observer.OnPageSkippedSize(currentUrl, finalUrl, bodyStream.Length + bytesRead, _context.MaxCrawlSizeBytes);
                    return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
                }

                bodyStream.Write(buffer, 0, bytesRead);

                if (!checkedPrefix && bodyStream.Length >= 4096)
                {
                    checkedPrefix = true;
                    var prefix = bodyStream.ToArray();
                    if (!CrawlPolicy.IsSupportedPrefix(prefix, contentType, finalUrl))
                    {
                        _context.Observer.OnPageSkippedType(currentUrl, finalUrl, contentType);
                        return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
                    }
                }
            }

            body = bodyStream.ToArray();

            if (!checkedPrefix)
            {
                if (!CrawlPolicy.IsSupportedPrefix(body, contentType, finalUrl))
                {
                    _context.Observer.OnPageSkippedType(currentUrl, finalUrl, contentType);
                    return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
                }
            }
        }

        string newHash = Convert.ToHexString(SHA256.HashData(body));
        var finalState = string.Equals(finalUrl, currentUrl, StringComparison.OrdinalIgnoreCase)
            ? state
            : await CrawlStore.GetCrawlStateAsync(_context.Read, finalUrl, cancellationToken);

        if (finalState.ContentHash is not null && finalState.ContentHash == newHash
            && await CrawlStore.UrlHasChunksAsync(_context.Read, finalUrl, cancellationToken))
        {
            _context.Observer.OnPageUnchangedHash(currentUrl, finalUrl);
            await EnqueueStoredOutlinksAsync(finalUrl, cancellationToken);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        string? duplicateOf = null;
        if (_context.IndexedContentHashes.TryGetValue(newHash, out var inRunUrl)
            && !string.Equals(inRunUrl, finalUrl, StringComparison.OrdinalIgnoreCase))
        {
            duplicateOf = inRunUrl;
        }
        duplicateOf ??= await CrawlStore.FindIndexedDuplicateAsync(_context.Read, newHash, finalUrl, cancellationToken);
        if (duplicateOf != null)
        {
            _context.Observer.OnPageDuplicateContent(currentUrl, finalUrl, duplicateOf);
            EnqueueSingle(duplicateOf);
            return new AliasJob(finalUrl, statusCode, redirectSourceUrl);
        }

        string? newETag = response.Headers.ETag?.Tag;
        string? newLastModified = response.Content.Headers.LastModified?.ToString("r");

        var kind = CrawlPolicy.ClassifyContent(contentType, body);

        if (kind == DocKind.Pdf)
        {
            var (pdfTitle, pdfText) = ContentExtractor.ExtractPdf(body);
            _context.IndexedContentHashes[newHash] = finalUrl;
            _context.Observer.OnPageIndexed(currentUrl, finalUrl, 0);
            return new IndexJob(finalUrl, statusCode, pdfTitle, pdfTitle ?? string.Empty, pdfText,
                newETag, newLastModified, newHash, Array.Empty<string>(), Array.Empty<string>(), redirectSourceUrl);
        }

        if (kind == DocKind.Docx)
        {
            var (docxTitle, docxText) = ContentExtractor.ExtractDocx(body);
            _context.IndexedContentHashes[newHash] = finalUrl;
            _context.Observer.OnPageIndexed(currentUrl, finalUrl, 0);
            return new IndexJob(finalUrl, statusCode, docxTitle, docxTitle ?? string.Empty, docxText,
                newETag, newLastModified, newHash, Array.Empty<string>(), Array.Empty<string>(), redirectSourceUrl);
        }

        if (kind != DocKind.Html)
        {
            _context.Observer.OnPageSkippedType(currentUrl, finalUrl, contentType);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        var xRobotsTag = response.Headers.TryGetValues("X-Robots-Tag", out var values)
            ? string.Join(",", values)
            : null;
        var analysis = ContentExtractor.AnalyzeHtml(body, response.Content.Headers.ContentType?.CharSet, xRobotsTag, finalUrl,
            _context.AllowedHosts, _context.RobotsCache, CrawlerService.UserAgent);

        if (analysis.CanonicalAlias != null)
        {
            _context.Observer.OnPageAlias(currentUrl, finalUrl, analysis.CanonicalAlias);
            EnqueueSingle(analysis.CanonicalAlias);
            return new AliasJob(finalUrl, statusCode, redirectSourceUrl);
        }

        _context.Observer.OnOutlinksAdded(analysis.Outlinks.Count);
        foreach (var link in analysis.Outlinks)
        {
            Discover(link);
        }

        if (analysis.NoIndex)
        {
            _context.Observer.OnPageNoIndex(currentUrl, finalUrl);
            return new NoIndexJob(finalUrl, statusCode, analysis.Title, newETag, newLastModified, newHash, analysis.Outlinks, analysis.OffsiteLinks, redirectSourceUrl);
        }

        _context.IndexedContentHashes[newHash] = finalUrl;
        _context.Observer.OnPageIndexed(currentUrl, finalUrl, analysis.Outlinks.Count);
        return new IndexJob(finalUrl, statusCode, analysis.Title, analysis.Headings, analysis.Text,
            newETag, newLastModified, newHash, analysis.Outlinks, analysis.OffsiteLinks, redirectSourceUrl);
    }

    private async Task<(RobotsRules Rules, bool Unavailable, bool Reachable)> GetRobotsRulesAsync(Uri baseUri, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            var robotsUrl = new Uri(baseUri, "/robots.txt");
            using var response = await _httpClient.GetAsync(robotsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var (body, truncated) = await ReadBodyLimitedAsync(response, maxBytes, cancellationToken);
                if (truncated)
                {
                    _logger.LogWarning("robots.txt for {Host} exceeds the {Limit}-byte limit; parsing the truncated prefix.", baseUri.Host, maxBytes);
                }
                return (RobotsRules.Parse(Encoding.UTF8.GetString(body), CrawlerService.UserAgent), false, true);
            }

            if ((int)response.StatusCode >= 500)
            {
                _logger.LogWarning("robots.txt for {Host} returned {Status}; treating as disallow-all.", baseUri.Host, (int)response.StatusCode);
                return (RobotsRules.DisallowAll, true, true);
            }

            return (RobotsRules.AllowAll, false, true);
        }
        catch (Exception ex)
        {
            bool reachable = !IsUnreachableError(ex, cancellationToken);
            _logger.LogWarning(ex, "Failed to fetch or parse robots.txt for {Host}.", baseUri.Host);
            return (RobotsRules.AllowAll, false, reachable);
        }
    }

    private static async Task<(byte[] Body, bool Truncated)> ReadBodyLimitedAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bodyStream = new MemoryStream();
        var buffer = new byte[8192];
        while (bodyStream.Length < maxBytes)
        {
            int toRead = (int)Math.Min(buffer.Length, maxBytes - bodyStream.Length);
            int bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (bytesRead == 0) return (bodyStream.ToArray(), false);
            bodyStream.Write(buffer, 0, bytesRead);
        }
        bool truncated = await responseStream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) > 0;
        return (bodyStream.ToArray(), truncated);
    }

    private async Task<(List<string> Locations, List<string> NestedSitemaps)> FetchSitemapAsync(string sitemapUrl, long maxBytes, CancellationToken cancellationToken)
    {
        var locations = new List<string>();
        var nested = new List<string>();
        try
        {
            using var response = await _httpClient.GetAsync(sitemapUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return (locations, nested);

            if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
            {
                _logger.LogWarning("Skipping sitemap {Url}: Content-Length ({Length} bytes) exceeds the {Limit}-byte limit.", sitemapUrl, declaredLength, maxBytes);
                return (locations, nested);
            }

            var (bytes, truncated) = await ReadBodyLimitedAsync(response, maxBytes, cancellationToken);
            if (truncated)
            {
                _logger.LogWarning("Skipping sitemap {Url}: body exceeds the {Limit}-byte limit.", sitemapUrl, maxBytes);
                return (locations, nested);
            }

            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            var doc = new XmlDocument { XmlResolver = null };
            using (var byteStream = new MemoryStream(bytes))
            using (var xmlReader = XmlReader.Create(byteStream, readerSettings))
            {
                doc.Load(xmlReader);
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

    private bool Discover(string url)
    {
        if (!_context.Visited.Add(url)) return false;
        _context.Queue.Enqueue(url);
        return true;
    }

    private void EnqueueSingle(string url)
    {
        if (_context.Visited.Add(url))
        {
            _context.Queue.Enqueue(url);
        }
    }

    private async Task EnqueueStoredOutlinksAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var links = await CrawlStore.GetStoredOutlinksAsync(_context.Read, url, cancellationToken);
            int added = 0;
            foreach (var link in links)
            {
                if (Discover(link)) added++;
            }
            if (added > 0)
            {
                _logger.LogDebug("Re-enqueued {Count} stored outlinks from unchanged page {Url}", added, url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read stored outlinks for unchanged page {Url}; subsequent pages may not be reached.", url);
        }
    }

    private async Task DelayForHostAsync(string host, TimeSpan minGap, CancellationToken cancellationToken)
    {
        DateTime lastFetch;
        lock (_context.LastFetchUtc)
        {
            _context.LastFetchUtc.TryGetValue(host, out lastFetch);
        }

        var now = DateTime.UtcNow;
        var elapsed = now - lastFetch;
        if (elapsed < minGap)
        {
            var delay = minGap - elapsed;
            await Task.Delay(delay, cancellationToken);
        }

        lock (_context.LastFetchUtc)
        {
            _context.LastFetchUtc[host] = DateTime.UtcNow;
        }
    }

    private static TimeSpan ResolveRequestDelay(RobotsRules robots)
    {
        if (!robots.CrawlDelaySeconds.HasValue)
        {
            return TimeSpan.FromMilliseconds(DefaultRequestDelayMs);
        }
        var delay = robots.CrawlDelaySeconds.Value;
        if (delay > MaxCrawlDelaySeconds)
        {
            return TimeSpan.FromSeconds(MaxCrawlDelaySeconds);
        }
        return TimeSpan.FromSeconds(delay);
    }

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

    private async Task<(LinkStatus Status, int StatusCode)> ProbeLinkAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendProbeAsync(url, cancellationToken);
            _context.HostHealth.RecordContacted(response.RequestMessage?.RequestUri?.Host ?? "");

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri != null)
            {
                var normalizedFinal = UrlNormalizer.Normalize(finalUri);
                if (!string.Equals(normalizedFinal, url, StringComparison.OrdinalIgnoreCase))
                {
                    return (LinkStatus.Redirect, (int)response.StatusCode);
                }
            }

            int code = (int)response.StatusCode;
            if (code is >= 400 and < 600) return (LinkStatus.Error, code);
            return (LinkStatus.Ok, code);
        }
        catch (Exception ex)
        {
            if (IsUnreachableError(ex, cancellationToken))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    _context.HostHealth.RecordUnreachable(uri.Host);
                }
                return (LinkStatus.Error, 503);
            }
            return (LinkStatus.Ok, 0);
        }
    }

    private async Task<HttpResponseMessage> SendProbeAsync(string url, CancellationToken cancellationToken)
    {
        var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch
        {
            var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            return await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.MethodNotAllowed || (int)response.StatusCode == 400)
        {
            var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            return await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        return response;
    }

    private static int CompareLinks(BrokenLink a, BrokenLink b)
    {
        int c = string.Compare(a.FoundOn, b.FoundOn, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        return string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReasonFor(LinkStatus status, int statusCode)
    {
        if (status == LinkStatus.Redirect)
        {
            return statusCode == 301 ? "301 Permanent Redirect" : "302 Temporary Redirect";
        }
        if (statusCode == 0) return "Connection Failed";
        if (statusCode == 404) return "404 Not Found";
        if (statusCode == 410) return "410 Gone";
        if (statusCode == 403) return "403 Forbidden";
        if (statusCode == 503) return "503 Service Unavailable";
        return $"HTTP {statusCode} Error";
    }
}
