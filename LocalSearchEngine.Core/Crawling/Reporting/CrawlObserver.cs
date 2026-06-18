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
    
    private CrawlPhase _currentPhase;

    public CrawlStats Stats { get; } = new CrawlStats();

    /// <summary>
    /// Returns the live count of unique URLs discovered so far (the crawl frontier). The orchestrator
    /// wires this to the crawl context's visited set; until then it reports zero.
    /// </summary>
    public Func<int> DiscoveredCount { get; set; } = () => 0;

    public CrawlObserver(ILogger logger, ICrawlReporter reporter, DateTime startedUtc)
    {
        _logger = logger;
        _reporter = reporter;
        _startedUtc = startedUtc;
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

    public void OnCrawlCancelled(int dispatchedCount)
    {
        _logger.LogInformation("Crawl cancelled after dispatching {Indexed} pages.", dispatchedCount);
    }

    public void OnHostCapReached(int cap, string host, string url)
    {
        _logger.LogInformation("Per-host cap ({Cap}) reached for {Host}; skipping {Url}", cap, host, url);
    }

    public void OnPageFetching(int indexedCount, int discoveredCount, string url)
    {
        _logger.LogInformation("Crawling ({Indexed} indexed / {Discovered} discovered): {Url}", indexedCount, discoveredCount, url);
    }

    public void OnFetchCancelled(string url)
    {
        _logger.LogInformation("Crawl cancelled while fetching {Url}.", url);
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

    public void OnSeedRedirectedToNewOrigin(string currentUrl, string newOrigin)
    {
        _logger.LogInformation("Seed {Seed} redirected to {Origin}; adding it to the allowed hosts.", currentUrl, newOrigin);
    }

    public void OnPageRedirectedOutScope(string currentUrl, string newUrl)
    {
        _logger.LogInformation("Redirect left the allowed hosts: {From} -> {To}", currentUrl, newUrl);
        ReportPage(currentUrl, CrawlOutcome.Redirected);
    }

    public void OnPageRedirectedDisallowed(string currentUrl, string newUrl)
    {
        _logger.LogInformation("Redirect target disallowed by robots.txt: {Url}", newUrl);
        ReportPage(currentUrl, CrawlOutcome.Redirected);
    }

    public void OnPageRedirectedAlreadySeen(string currentUrl, string newUrl)
    {
        _logger.LogInformation("Redirected to already-seen URL: {Url}", newUrl);
        ReportPage(currentUrl, CrawlOutcome.Redirected);
    }

    public void OnPageGone(string currentUrl, string finalUrl, int statusCode)
    {
        _logger.LogInformation("Page gone ({StatusCode}): {Url} — removing from index.", statusCode, finalUrl);
        ReportPage(currentUrl, CrawlOutcome.Gone);
    }

    public void OnPageFailed(string currentUrl, string finalUrl, int statusCode)
    {
        _logger.LogWarning("Failed to crawl {Url} with status code {StatusCode}; keeping existing index.", finalUrl, statusCode);
        ReportPage(currentUrl, CrawlOutcome.Failed);
    }

    public void OnPageSkippedSize(string currentUrl, string finalUrl, long contentLength, long limit)
    {
        _logger.LogWarning("Skipping {Url}: Content-Length ({Length} bytes) exceeds maximum limit of {Limit} bytes.", finalUrl, contentLength, limit);
        ReportPage(currentUrl, CrawlOutcome.SkippedSize);
    }

    public void OnPageSkippedType(string currentUrl, string finalUrl, string? contentType)
    {
        _logger.LogInformation("Skipping {Url}: Content-Type '{ContentType}' is not whitelisted for indexing.", finalUrl, contentType);
        ReportPage(currentUrl, CrawlOutcome.SkippedType);
    }

    public void OnPageUnchanged(string currentUrl)
    {
        _logger.LogInformation("Page not modified since last crawl (304): {Url}", currentUrl);
        ReportPage(currentUrl, CrawlOutcome.Unchanged);
    }

    public void OnPageUnchangedHash(string currentUrl, string finalUrl)
    {
        _logger.LogInformation("Content unchanged since last crawl (hash match): {Url}", finalUrl);
        ReportPage(currentUrl, CrawlOutcome.Unchanged);
    }

    public void OnPageDuplicateContent(string currentUrl, string finalUrl, string canonicalUrl)
    {
        _logger.LogInformation("Duplicate content: {Url} matches already-indexed {Canonical}; not indexing a copy.", finalUrl, canonicalUrl);
        ReportPage(currentUrl, CrawlOutcome.Redirected);
    }

    public void OnPageAlias(string currentUrl, string finalUrl, string canonicalUrl)
    {
        _logger.LogInformation("Canonical alias: {Url} -> {Canonical}", finalUrl, canonicalUrl);
        ReportPage(currentUrl, CrawlOutcome.Redirected);
    }

    public void OnPageDisallowed(string currentUrl)
    {
        _logger.LogInformation("Disallowed by robots.txt: {Url}", currentUrl);
        ReportPage(currentUrl, CrawlOutcome.Disallowed);
    }

    public void OnPageNoIndex(string currentUrl, string finalUrl)
    {
        _logger.LogInformation("noindex directive: {Url} — not indexing its content.", finalUrl);
        ReportPage(currentUrl, CrawlOutcome.NoIndex);
    }

    public void OnPageIndexed(string currentUrl, string finalUrl, int outlinksCount)
    {
        ReportPage(currentUrl, CrawlOutcome.Indexed);
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
