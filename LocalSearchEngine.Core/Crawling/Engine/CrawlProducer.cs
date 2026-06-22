using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.Crawling.Extraction;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// The producer half of the crawl. It drains the frontier and, for each URL, runs the gauntlet of
/// checks — scope, robots.txt, the per-host cap, host reachability, and a politeness delay — before
/// downloading the page with <see cref="PageDownloader"/> and classifying the outcome. Successful HTML
/// is run through <see cref="Extraction.ContentExtractor"/>, deduplicated by content hash, and has its
/// outlinks fed back into the frontier; every URL becomes a <see cref="CrawlJob"/> describing what
/// should be persisted, written to the shared channel for the consumer to apply. Index writes are
/// deliberately left to the consumer — the producer only reads existing crawl state, for conditional
/// requests and duplicate detection — apart from the post-crawl pruning pass, which runs after the
/// consumer has drained and so can write safely.
/// </summary>
internal sealed class CrawlProducer
{
    /// <summary>The default request delay in milliseconds when robots.txt does not specify one.</summary>
    private const int DefaultRequestDelayMs = 250;
    /// <summary>The maximum allowed crawl delay in seconds to prevent excessive waiting.</summary>
    private const int MaxCrawlDelaySeconds = 30;

    /// <summary>The vector search service to record and update vector index chunks.</summary>
    private readonly VectorSearchService _vectorSearchService;
    /// <summary>The channel writer used to send finished jobs to the consumer.</summary>
    private readonly ChannelWriter<CrawlJob> _writer;
    /// <summary>The shared crawl context holding queue and visited state.</summary>
    private readonly CrawlContext _context;
    /// <summary>Service to fetch and evaluate robots.txt rules.</summary>
    private readonly RobotsService _robotsService;
    /// <summary>Downloader to fetch web content and apply page rules.</summary>
    private readonly PageDownloader _pageDownloader;
    /// <summary>The logger instance.</summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrawlProducer"/> class.
    /// </summary>
    /// <param name="vectorSearchService">The vector search service provider.</param>
    /// <param name="writer">The channel writer to submit crawl jobs.</param>
    /// <param name="context">The active crawl context.</param>
    /// <param name="robotsService">The robots.txt service handler.</param>
    /// <param name="pageDownloader">The page downloader service.</param>
    /// <param name="logger">The logger instance.</param>
    public CrawlProducer(
        VectorSearchService vectorSearchService,
        ChannelWriter<CrawlJob> writer,
        CrawlContext context,
        RobotsService robotsService,
        PageDownloader pageDownloader,
        ILogger logger)
    {
        _vectorSearchService = vectorSearchService;
        _writer = writer;
        _context = context;
        _robotsService = robotsService;
        _pageDownloader = pageDownloader;
        _logger = logger;
    }

    /// <summary>
    /// Executes the main crawl loop, fetching pages from the queue and producing crawl jobs.
    /// </summary>
    /// <param name="maxPages">The maximum total pages to index.</param>
    /// <param name="maxPagesPerHost">The maximum pages allowed per individual host.</param>
    /// <returns>A tuple containing the number of produced jobs and the number of successfully indexed pages.</returns>
    public async Task<(int ProducedJobs, int IndexedCount)> ProduceAsync(int maxPages, int maxPagesPerHost)
    {
        int indexedCount = 0;
        int producedJobs = 0;

        _context.Observer.OnPhaseChanged(CrawlPhase.Crawling);
        while (_context.Queue.Count > 0 && indexedCount < maxPages)
        {
            var currentUrl = _context.Queue.Dequeue();

            if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentHostUri)
                && _context.IndexedPerHost.TryGetValue(currentHostUri.Host, out var hostIndexed)
                && hostIndexed >= maxPagesPerHost)
            {
                _context.HostCapSkipped = true;
                _context.Observer.OnHostCapReached(maxPagesPerHost, currentHostUri.Host, currentUrl);
                continue;
            }

            if (currentHostUri is not null && _context.HostHealth.IsUnreachable(currentHostUri.Host))
            {
                continue;
            }

            _context.Observer.OnPageFetching(indexedCount, _context.Visited.Count, currentUrl);

            CrawlJob? job;
            try
            {
                job = await ProduceJobAsync(currentUrl);
            }
            catch (Exception ex)
            {
                _context.Observer.OnFetchError(ex, currentUrl);
                job = new TouchJob(currentUrl, 500);
            }

            if (job is not null)
            {
                producedJobs++;
                await _writer.WriteAsync(job);
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
    /// Removes index entries for in-scope URLs the completed crawl never reached.
    /// </summary>
    /// <param name="crawlStartUtc">The UTC timestamp indicating when the crawl started.</param>
    /// <returns>The number of stale URLs pruned from storage.</returns>
    public async Task<int> PruneStaleUrlsAsync(DateTime crawlStartUtc)
    {
        int pruned = 0;
        try
        {
            var candidates = await CrawlStore.GetUrlsNotCrawledSinceAsync(_context.Read, crawlStartUtc);
            foreach (var url in candidates)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                if (!_context.AllowedHosts.IsAllowed(uri)) continue;
                if (_context.RobotsUnavailable.Contains(UrlOrigin.Key(uri))) continue;
                if (_context.HostHealth.IsUnreachable(uri.Host)) continue;

                // NOTE: Writes directly to _context.Write are safe here because this post-crawl cleanup phase
                // runs after the main crawl consumer task has fully completed.
                await _vectorSearchService.DeleteUrlChunksAsync(url);
                await CrawlStore.DeleteLinksAsync(_context.Write, url);
                await CrawlStore.DeleteCrawlStateAsync(_context.Write, url);
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
    /// Processes a single URL: checks robot rules, downloads the page, and constructs the appropriate crawl job.
    /// </summary>
    /// <param name="currentUrl">The URL to crawl.</param>
    /// <returns>A <see cref="CrawlJob"/> representing the results of processing, or <c>null</c> if skipped.</returns>
    private async Task<CrawlJob?> ProduceJobAsync(string currentUrl)
    {
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri)) return null;
        if (!_context.AllowedHosts.IsAllowed(currentUri))
        {
            _context.Observer.OnOutScopeUrlReached(currentUrl);
            return null;
        }

        var currentRobots = await _robotsService.GetOrFetchRobotsAsync(currentUri, _context);

        if (_context.HostHealth.IsUnreachable(currentUri.Host))
        {
            return null;
        }

        if (!CrawlPolicy.IsAllowedByRobots(currentUrl, currentRobots))
        {
            _context.Observer.OnPageDisallowed(currentUrl);
            return null;
        }
        await DelayForHostAsync(currentUri.Host, ResolveRequestDelay(currentRobots));

        var state = await CrawlStore.GetCrawlStateAsync(_context.Read, currentUrl);

        // Functional delegate to validate redirected URLs during streaming
        Func<Uri, Task<bool>> redirectValidator = async (finalUri) =>
        {
            var normalizedFinal = UrlNormalizer.Normalize(finalUri);
            if (!_context.AllowedHosts.IsAllowed(finalUri))
            {
                if (string.Equals(currentUrl, _context.SeedUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _context.Observer.OnSeedRedirectedToNewOrigin(currentUrl, UrlOrigin.Key(finalUri));
                    _context.AllowedHosts.AddOrigin(finalUri);
                    return true;
                }
                else
                {
                    _context.Observer.OnPageRedirectedOutScope(currentUrl, normalizedFinal);
                    return false;
                }
            }

            var finalRobots = await _robotsService.GetOrFetchRobotsAsync(finalUri, _context);
            if (!CrawlPolicy.IsAllowedByRobots(normalizedFinal, finalRobots))
            {
                _context.Observer.OnPageRedirectedDisallowed(currentUrl, normalizedFinal);
                return false;
            }

            if (!_context.Visited.Add(normalizedFinal))
            {
                _context.Observer.OnPageRedirectedAlreadySeen(currentUrl, normalizedFinal);
                return false;
            }

            return true;
        };

        // A user noindex rule means we re-fetch the page in full every run: we must re-extract its
        // links and ensure any content indexed before the rule existed is dropped. Skip conditional
        // request headers so the server can't answer 304 with no body. (Matched on the requested URL;
        // the post-redirect URL is checked again before indexing.)
        bool suppressConditional = _context.NoIndexRules.Matches(currentUrl);
        var condETag = suppressConditional ? null : state.ETag;
        var condLastModified = suppressConditional ? null : state.LastModified;
        var downloadResult = await _pageDownloader.DownloadAsync(currentUrl, condETag, condLastModified, _context.MaxCrawlSizeBytes, redirectValidator);

        // Reachability for this host was already recorded when its robots.txt was fetched above,
        // and an unreachable host would have been skipped before we got here. The tracker never
        // writes off a host that has answered, so a per-download recording can't change anything.
        int statusCode = (int)downloadResult.StatusCode;

        switch (downloadResult.Status)
        {
            case DownloadStatus.NotModified:
                _context.Observer.OnPageUnchanged(currentUrl);
                await EnqueueStoredOutlinksAsync(currentUrl);
                return new TouchJob(currentUrl, statusCode);

            case DownloadStatus.RedirectBlocked:
                return new AliasJob(currentUrl, statusCode);

            case DownloadStatus.Gone:
                var goneUrl = downloadResult.FinalRequestUri?.ToString() ?? currentUrl;
                _context.Observer.OnPageGone(currentUrl, goneUrl, statusCode);
                return new GoneJob(goneUrl, statusCode, string.Equals(goneUrl, currentUrl, StringComparison.OrdinalIgnoreCase) ? null : currentUrl);

            case DownloadStatus.Failed:
                var failedUrl = downloadResult.FinalRequestUri?.ToString() ?? currentUrl;
                _context.Observer.OnPageFailed(currentUrl, failedUrl, statusCode);
                return new TouchJob(failedUrl, statusCode, string.Equals(failedUrl, currentUrl, StringComparison.OrdinalIgnoreCase) ? null : currentUrl);

            case DownloadStatus.SizeLimitExceeded:
                var sizeUrl = downloadResult.FinalRequestUri?.ToString() ?? currentUrl;
                _context.Observer.OnPageSkippedSize(currentUrl, sizeUrl, downloadResult.SizeRead, _context.MaxCrawlSizeBytes);
                return new TouchJob(sizeUrl, statusCode, string.Equals(sizeUrl, currentUrl, StringComparison.OrdinalIgnoreCase) ? null : currentUrl);

            case DownloadStatus.UnsupportedType:
                var typeUrl = downloadResult.FinalRequestUri?.ToString() ?? currentUrl;
                _context.Observer.OnPageSkippedType(currentUrl, typeUrl, downloadResult.ContentType);
                return new TouchJob(typeUrl, statusCode, string.Equals(typeUrl, currentUrl, StringComparison.OrdinalIgnoreCase) ? null : currentUrl);

            case DownloadStatus.Success:
                return await ProcessDownloadSuccessAsync(currentUrl, state, downloadResult);

            default:
                throw new InvalidOperationException($"Unhandled download status: {downloadResult.Status}");
        }
    }

    /// <summary>
    /// Processes a successfully downloaded page or file, verifying duplicate content, extracting content/metadata, and scheduling the appropriate indexing job.
    /// </summary>
    /// <param name="currentUrl">The request URL.</param>
    /// <param name="state">The previously recorded crawl state of the URL.</param>
    /// <param name="downloadResult">The result details of the successful download.</param>
    /// <returns>A <see cref="CrawlJob"/> representing the classification of the download success.</returns>
    private async Task<CrawlJob?> ProcessDownloadSuccessAsync(
        string currentUrl,
        (string? ETag, string? LastModified, string? ContentHash) state,
        DownloadResult downloadResult)
    {
        var finalUrl = downloadResult.FinalRequestUri != null
            ? UrlNormalizer.Normalize(downloadResult.FinalRequestUri)
            : currentUrl;

        var redirectSourceUrl = string.Equals(finalUrl, currentUrl, StringComparison.OrdinalIgnoreCase)
            ? null
            : currentUrl;

        // A user-configured noindex rule (matched on the final URL) forces "follow, don't index": the
        // page is still parsed and its links followed, but the same-hash/duplicate/canonical shortcuts
        // are skipped and its content is never indexed — exactly as if the page declared noindex itself.
        bool userNoIndex = _context.NoIndexRules.Matches(finalUrl);

        int statusCode = (int)downloadResult.StatusCode;
        byte[] body = downloadResult.Body ?? Array.Empty<byte>();

        string? newETag = downloadResult.ETag;
        string? newLastModified = downloadResult.LastModified;

        var finalState = string.Equals(finalUrl, currentUrl, StringComparison.OrdinalIgnoreCase)
            ? state
            : await CrawlStore.GetCrawlStateAsync(_context.Read, finalUrl);

        var kind = CrawlPolicy.ClassifyContent(downloadResult.ContentType, body);

        // PDF/DOCX carry no in-scope outlinks to follow, so a noindex rule simply drops them from the
        // index (and removes any prior entry). The empty link sets clear out stale links via NoIndexJob.
        if (kind == DocKind.Pdf)
        {
            if (userNoIndex)
            {
                _context.Observer.OnPageNoIndex(currentUrl, finalUrl);
                return new NoIndexJob(finalUrl, statusCode, null, newETag, newLastModified, null,
                    Array.Empty<string>(), Array.Empty<string>(), kind, redirectSourceUrl);
            }
            var pdf = ContentExtractor.ExtractPdf(body);
            // A PDF whose text came out as font-encoding garbage (or that has no text layer at all) is
            // worse than useless in the index: drop it the same way a noindex page is dropped, but flag
            // it distinctly so the run stats show how much of the PDF corpus is unreadable.
            if (pdf.IsLowQualityText)
            {
                _context.Observer.OnPageLowQualityText(currentUrl, finalUrl, pdf.MappableFraction, pdf.TotalGlyphs);
                return new NoIndexJob(finalUrl, statusCode, pdf.Title, newETag, newLastModified, null,
                    Array.Empty<string>(), Array.Empty<string>(), kind, redirectSourceUrl);
            }
            return await EmitIndexableAsync(currentUrl, finalUrl, redirectSourceUrl, statusCode, finalState,
                pdf.Title, pdf.Title ?? string.Empty, pdf.Text, newETag, newLastModified,
                Array.Empty<string>(), Array.Empty<string>(), kind);
        }

        if (kind == DocKind.Docx)
        {
            if (userNoIndex)
            {
                _context.Observer.OnPageNoIndex(currentUrl, finalUrl);
                return new NoIndexJob(finalUrl, statusCode, null, newETag, newLastModified, null,
                    Array.Empty<string>(), Array.Empty<string>(), kind, redirectSourceUrl);
            }
            var (docxTitle, docxText) = ContentExtractor.ExtractDocx(body);
            return await EmitIndexableAsync(currentUrl, finalUrl, redirectSourceUrl, statusCode, finalState,
                docxTitle, docxTitle ?? string.Empty, docxText, newETag, newLastModified,
                Array.Empty<string>(), Array.Empty<string>(), kind);
        }

        if (kind != DocKind.Html)
        {
            _context.Observer.OnPageSkippedType(currentUrl, finalUrl, downloadResult.ContentType);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        var xRobotsTag = downloadResult.XRobotsTag;
        var analysis = ContentExtractor.AnalyzeHtml(body, downloadResult.CharSet, xRobotsTag, finalUrl,
            _context.AllowedHosts, _context.RobotsCache, CrawlerService.UserAgent);

        // A noindex rule means "follow, don't index", so honoring a canonical alias here would be
        // wrong: aliasing skips this page's own links. Fall through to follow them instead.
        if (!userNoIndex && analysis.CanonicalAlias != null)
        {
            _context.Observer.OnPageAlias(currentUrl, finalUrl, analysis.CanonicalAlias);
            _context.EnqueueSingle(analysis.CanonicalAlias);
            return new AliasJob(finalUrl, statusCode, redirectSourceUrl);
        }

        _context.Observer.OnOutlinksAdded(analysis.Outlinks.Count);
        foreach (var link in analysis.Outlinks)
        {
            _context.Discover(link);
        }

        // The noindex decision is made before the unchanged/duplicate shortcuts below so that a page
        // which newly declares noindex — even via an X-Robots-Tag header that leaves the body byte for
        // byte identical — still drops any chunks indexed before the directive existed.
        if (userNoIndex || analysis.NoIndex)
        {
            _context.Observer.OnPageNoIndex(currentUrl, finalUrl);
            return new NoIndexJob(finalUrl, statusCode, analysis.Title, newETag, newLastModified, null,
                analysis.Outlinks, analysis.OffsiteLinks, kind, redirectSourceUrl);
        }

        return await EmitIndexableAsync(currentUrl, finalUrl, redirectSourceUrl, statusCode, finalState,
            analysis.Title, analysis.Headings, analysis.Text, newETag, newLastModified,
            analysis.Outlinks, analysis.OffsiteLinks, kind);
    }

    /// <summary>
    /// Finishes an extracted, indexable page: hashes the content that would be embedded and uses it to
    /// short-circuit needless work — a <see cref="TouchJob"/> when this URL's stored content is unchanged,
    /// an <see cref="AliasJob"/> when the same content is already indexed under another URL — otherwise an
    /// <see cref="IndexJob"/> that (re)embeds it. Shared by the HTML, PDF, and DOCX paths.
    /// </summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="finalUrl">The URL after redirects/normalization.</param>
    /// <param name="redirectSourceUrl">The dequeued URL when a redirect was followed; otherwise <c>null</c>.</param>
    /// <param name="statusCode">The HTTP status code of the response.</param>
    /// <param name="finalState">The crawl state previously recorded for <paramref name="finalUrl"/>.</param>
    /// <param name="title">The extracted document/page title.</param>
    /// <param name="headings">The extracted heading text.</param>
    /// <param name="text">The extracted main text.</param>
    /// <param name="newETag">The response ETag, if any.</param>
    /// <param name="newLastModified">The response Last-Modified header, if any.</param>
    /// <param name="outlinks">The in-scope outlinks discovered on the page.</param>
    /// <param name="offsiteLinks">The off-site links discovered on the page.</param>
    /// <param name="kind">The classified document kind (Html/Pdf/Docx) of the page being indexed.</param>
    /// <returns>The crawl job to persist for this page.</returns>
    private async Task<CrawlJob> EmitIndexableAsync(
        string currentUrl, string finalUrl, string? redirectSourceUrl, int statusCode,
        (string? ETag, string? LastModified, string? ContentHash) finalState,
        string? title, string headings, string text,
        string? newETag, string? newLastModified,
        IReadOnlyCollection<string> outlinks, IReadOnlyCollection<string> offsiteLinks,
        DocKind kind)
    {
        string contentHash = ComputeContentHash(title, headings, text);

        // The fallback for servers that ignore ETag/If-Modified-Since and answer 200 with an unchanged
        // page: if what we'd embed is identical to what's already indexed for this URL, skip the
        // re-embed (the costly step) and just stamp the visit. Hashing the extracted text rather than
        // the raw bytes is what makes this reliable — per-request markup noise (CSP nonces, CSRF
        // tokens, timestamps) no longer forces a needless re-index. Outlinks were already discovered
        // by the caller, so the frontier is unaffected by returning early here.
        if (finalState.ContentHash == contentHash
            && await CrawlStore.UrlHasChunksAsync(_context.Read, finalUrl))
        {
            _context.Observer.OnPageUnchangedHash(currentUrl, finalUrl);
            return new TouchJob(finalUrl, statusCode, redirectSourceUrl);
        }

        // The same content is already indexed under a different URL: alias to it and crawl the
        // original rather than embedding a second copy.
        string? duplicateOf = null;
        if (_context.IndexedContentHashes.TryGetValue(contentHash, out var inRunUrl)
            && !string.Equals(inRunUrl, finalUrl, StringComparison.OrdinalIgnoreCase))
        {
            duplicateOf = inRunUrl;
        }
        duplicateOf ??= await CrawlStore.FindIndexedDuplicateAsync(_context.Read, contentHash, finalUrl);
        if (duplicateOf != null)
        {
            _context.Observer.OnPageDuplicateContent(currentUrl, finalUrl, duplicateOf);
            _context.EnqueueSingle(duplicateOf);
            return new AliasJob(finalUrl, statusCode, redirectSourceUrl);
        }

        _context.IndexedContentHashes[contentHash] = finalUrl;
        _context.Observer.OnPageIndexed(currentUrl, finalUrl, outlinks.Count);
        return new IndexJob(finalUrl, statusCode, title, headings, text,
            newETag, newLastModified, contentHash, outlinks, offsiteLinks, kind, redirectSourceUrl);
    }

    /// <summary>
    /// Hashes the extracted, indexable fields of a page — what actually gets embedded — rather than the
    /// raw response bytes. The fields are domain-separated so that moving text between the title,
    /// headings, and body changes the hash. This is the basis for both the unchanged-page shortcut and
    /// cross-URL duplicate detection, so two responses index to the same hash exactly when they would
    /// produce the same index entry.
    /// </summary>
    /// <param name="title">The extracted title, if any.</param>
    /// <param name="headings">The extracted heading text.</param>
    /// <param name="text">The extracted main text.</param>
    /// <returns>The uppercase hex SHA-256 of the combined fields.</returns>
    private static string ComputeContentHash(string? title, string headings, string text)
    {
        var canonical = string.Concat(title, "\n", headings, "\n", text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Reads outlinks stored in the database for an unchanged page and enqueues them for crawling.
    /// </summary>
    /// <param name="url">The URL of the unchanged page.</param>
    private async Task EnqueueStoredOutlinksAsync(string url)
    {
        try
        {
            var links = await CrawlStore.GetStoredOutlinksAsync(_context.Read, url);
            int added = 0;
            foreach (var link in links)
            {
                if (_context.Discover(link)) added++;
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

    /// <summary>
    /// Enforces host crawl delay politely by delaying execution if the last contact was too recent.
    /// </summary>
    /// <param name="host">The host name to check.</param>
    /// <param name="minGap">The minimum gap/duration between requests to this host.</param>
    private async Task DelayForHostAsync(string host, TimeSpan minGap)
    {
        _context.LastFetchUtc.TryGetValue(host, out var lastFetch);

        var now = DateTime.UtcNow;
        var elapsed = now - lastFetch;
        if (elapsed < minGap)
        {
            var delay = minGap - elapsed;
            await Task.Delay(delay);
        }

        _context.LastFetchUtc[host] = DateTime.UtcNow;
    }

    /// <summary>
    /// Resolves the request delay based on robots.txt rules, falling back to a default value if not specified.
    /// </summary>
    /// <param name="robots">The robots.txt rules for the host.</param>
    /// <returns>A <see cref="TimeSpan"/> representing the delay duration.</returns>
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
}
