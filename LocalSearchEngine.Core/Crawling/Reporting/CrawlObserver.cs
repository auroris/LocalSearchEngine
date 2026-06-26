namespace LocalSearchEngine.Core.Crawling.Reporting;

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

/// <summary>
/// The single sink for crawl events: each <c>On…</c> callback both writes the run log and updates
/// the running <see cref="CrawlStats"/>, and the page-outcome callbacks additionally push a fresh
/// snapshot to the <see cref="ICrawlReporter"/> driving the live display. Centralizing this here
/// keeps the crawl engine free of logging and presentation concerns. See <see cref="ICrawlObserver"/>
/// for what each callback signifies.
/// </summary>
internal sealed class CrawlObserver : ICrawlObserver
{
    private readonly ILogger _logger;
    private readonly ICrawlReporter _reporter;
    private readonly DateTime _startedUtc;
    private readonly CrawlHeartbeat _heartbeat;

    private CrawlPhase _currentPhase;

    public CrawlStats Stats { get; } = new CrawlStats();

    /// <summary>
    /// Returns the live count of unique URLs discovered so far (the crawl frontier). The orchestrator
    /// wires this to the crawl context's visited set; until then it reports zero.
    /// </summary>
    public Func<int> DiscoveredCount { get; set; } = () => 0;

    public CrawlObserver(ILogger logger, ICrawlReporter reporter, DateTime startedUtc, CrawlHeartbeat heartbeat)
    {
        _logger = logger;
        _reporter = reporter;
        _startedUtc = startedUtc;
        _heartbeat = heartbeat;
        _currentPhase = CrawlPhase.Starting;
    }

    private CrawlStatsSnapshot Snapshot() =>
        Stats.Snapshot(_currentPhase, DiscoveredCount(), DateTime.UtcNow - _startedUtc);

    private void ReportPage(string url, CrawlOutcome outcome)
    {
        Stats.Record(outcome);
        _reporter.PageProcessed(url, outcome, Snapshot());
    }

    public void OnPhaseChanged(CrawlPhase phase)
    {
        _currentPhase = phase;
        _heartbeat.MarkCrawler(phase.ToString());
        _reporter.PhaseChanged(phase, Snapshot());
    }

    public void OnOutlinksAdded(int count)
    {
        Stats.AddLinks(count);
    }

    public void OnSeedInvalid(string seedUrl)
    {
        _logger.LogError("Invalid seed URL: {Url}", seedUrl);
    }

    public void OnSeedUnreachable(string host)
    {
        _logger.LogError("Seed host {Host} is unreachable (connection failed on first contact); nothing to crawl.", host);
    }

    public void OnSeedDisallowed(string url)
    {
        _logger.LogWarning("Seed URL is disallowed by robots.txt: {Url}", url);
    }

    public void OnHostCapReached(int cap, string host, string url)
    {
        _logger.LogInformation("Per-host cap ({Cap}) reached for {Host}; skipping {Url}", cap, host, url);
    }

    public void OnPageFetching(int indexedCount, int discoveredCount, string url)
    {
        _heartbeat.MarkCrawler($"fetching {url}");
        _logger.LogInformation("Crawling ({Indexed} indexed / {Discovered} discovered): {Url}", indexedCount, discoveredCount, url);
    }

    public void OnFetchError(Exception ex, string url)
    {
        _logger.LogError(ex, "Error occurred while crawling {Url}", url);
        ReportPage(url, CrawlOutcome.Failed);
    }

    public void OnCrawlCompleted(string seedUrl)
    {
        _logger.LogInformation("Crawling completed for {SeedUrl} ({Indexed} pages indexed this run).", seedUrl, Stats.Indexed);
    }

    public void OnOutScopeUrlReached(string url)
    {
        _logger.LogWarning("Out-of-scope URL reached the frontier: {Url}", url);
    }

    public void OnPageRedirected(string currentUrl, string targetUrl)
    {
        _logger.LogInformation("Redirect: {From} -> {To}; queued the target.", currentUrl, targetUrl);
        ReportPage(currentUrl, CrawlOutcome.Redirected);
    }

    public void OnSeedRedirectedToNewOrigin(string currentUrl, string newOrigin)
    {
        _logger.LogInformation("Seed {Seed} redirected to {Origin}; adding it to the allowed hosts.", currentUrl, newOrigin);
        ReportPage(currentUrl, CrawlOutcome.Redirected);
    }

    public void OnPageRedirectedOutScope(string currentUrl, string newUrl)
    {
        _logger.LogInformation("Redirect left the allowed hosts: {From} -> {To}", currentUrl, newUrl);
        ReportPage(currentUrl, CrawlOutcome.Redirected);
    }

    public void OnPageGone(string url, int statusCode)
    {
        _logger.LogInformation("Page gone ({StatusCode}): {Url} — removing from index.", statusCode, url);
        ReportPage(url, CrawlOutcome.Gone);
    }

    public void OnPageFailed(string url, int statusCode)
    {
        _logger.LogWarning("Failed to crawl {Url} with status code {StatusCode}; keeping existing index.", url, statusCode);
        ReportPage(url, CrawlOutcome.Failed);
    }

    public void OnPageSkippedSize(string url, long contentLength, long limit)
    {
        _logger.LogWarning("Skipping {Url}: Content-Length ({Length} bytes) exceeds maximum limit of {Limit} bytes.", url, contentLength, limit);
        ReportPage(url, CrawlOutcome.SkippedSize);
    }

    public void OnPageSkippedType(string url, string? contentType)
    {
        _logger.LogInformation("Skipping {Url}: Content-Type '{ContentType}' is not whitelisted for indexing.", url, contentType);
        ReportPage(url, CrawlOutcome.SkippedType);
    }

    public void OnPageUnchanged(string url)
    {
        _logger.LogInformation("Page not modified since last crawl (304): {Url}", url);
        ReportPage(url, CrawlOutcome.Unchanged);
    }

    public void OnPageUnchangedHash(string url)
    {
        _logger.LogInformation("Content unchanged since last crawl (hash match): {Url}", url);
        ReportPage(url, CrawlOutcome.Unchanged);
    }

    public void OnPageDuplicateContent(string url, string canonicalUrl)
    {
        _logger.LogInformation("Duplicate content: {Url} matches already-indexed {Canonical}; not indexing a copy.", url, canonicalUrl);
        ReportPage(url, CrawlOutcome.Redirected);
    }

    public void OnPageAlias(string url, string canonicalUrl)
    {
        _logger.LogInformation("Canonical alias: {Url} -> {Canonical}", url, canonicalUrl);
        ReportPage(url, CrawlOutcome.Redirected);
    }

    public void OnPageDisallowed(string currentUrl)
    {
        _logger.LogInformation("Disallowed by robots.txt: {Url}", currentUrl);
        ReportPage(currentUrl, CrawlOutcome.Disallowed);
    }

    public void OnPageNoIndex(string url)
    {
        _logger.LogInformation("noindex directive: {Url} — not indexing its content.", url);
        ReportPage(url, CrawlOutcome.NoIndex);
    }

    public void OnPageLowQualityText(string url, double mappableFraction, long totalGlyphs)
    {
        if (totalGlyphs == 0)
            _logger.LogInformation("No extractable text layer in {Url} (likely a scanned image) — not indexing.", url);
        else
            _logger.LogInformation("Unreliable text extraction for {Url}: only {Mappable:P0} of glyphs map to Unicode — not indexing.", url, mappableFraction);
        ReportPage(url, CrawlOutcome.LowQualityText);
    }

    public void OnPageIndexed(string url, int outlinksCount)
    {
        // The counterpart to the unchanged/duplicate/noindex outcome lines: without it an embedded
        // page is the one outcome that leaves no trace in the log, making it impossible to tell from
        // the log which pages were re-embedded versus skipped by the content-hash fallback on a re-crawl.
        _logger.LogInformation("Indexed {Url} ({Outlinks} outlinks).", url, outlinksCount);
        ReportPage(url, CrawlOutcome.Indexed);
    }

    public void OnStaleUrlsPruned(int count)
    {
        if (count > 0)
        {
            _logger.LogInformation("Pruned {Count} stale URLs the completed crawl no longer reaches.", count);
            Stats.AddRemovedStale(count);
        }
    }

    public void OnPruneFailed(Exception ex)
    {
        _logger.LogError(ex, "Failed to prune stale URLs.");
    }

    public void OnBannedUrlsRemoved(int count)
    {
        if (count > 0)
        {
            _logger.LogInformation("Removed {Count} indexed URL(s) now disallowed by robots.txt.", count);
            Stats.AddRemovedBanned(count);
        }
    }

    public void OnRemoveBannedFailed(Exception ex)
    {
        _logger.LogError(ex, "Failed to remove robots-disallowed URLs.");
    }

    public void OnLinksVerified(int probed, int broken, int redirected)
    {
        _logger.LogInformation("Link verification: probed {Probed} undetermined link(s) — {Broken} broken, {Redirected} redirected.", probed, broken, redirected);
    }

    public void OnLinkProbeInconclusive(Exception ex, string url)
    {
        _logger.LogDebug(ex, "Inconclusive probe for link {Url}; treating it as resolved.", url);
    }

    public void OnAllowedServerIgnored(string entry)
    {
        _logger.LogWarning("Ignoring allowed-server entry '{Entry}': expected [scheme://]host[:port].", entry);
    }

    public void OnNoIndexPatternIgnored(string pattern)
    {
        _logger.LogWarning("Ignoring noindex pattern '{Pattern}': it is blank.", pattern);
    }
}
