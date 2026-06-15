namespace LocalSearchEngine.Core.Crawling.Reporting;

using System;
using Microsoft.Extensions.Logging;

/// <summary>
/// A stateful, per-crawl observer that centralizes all logging, statistics gathering, and progress reporting.
/// Implementations handle the translation of crawler domain events into log messages, stat increments, and 
/// <see cref="ICrawlReporter"/> callbacks.
/// </summary>
public interface ICrawlObserver
{
    /// <summary>Gets the finalized stats for the crawl.</summary>
    CrawlStats Stats { get; }
    
    /// <summary>Gets the list of broken links found during the crawl.</summary>
    IReadOnlyList<BrokenLink> BrokenLinks { get; }

    // Phase management
    void OnPhaseChanged(CrawlPhase phase);

    // Frontier tracking
    void OnUrlDiscovered(string targetUrl, string sourceUrl);
    void OnOutlinksAdded(int count);
    void OnOffsiteLinkDiscovered(string targetUrl, string sourceUrl);

    // Initial/High-level events
    void OnSeedInvalid(string seedUrl);
    void OnSeedUnreachable(string host);
    void OnSeedDisallowed(string url);
    void OnCrawlCancelled(int dispatchedCount);
    void OnHostCapReached(int cap, string host, string url);
    void OnPageFetching(int indexedCount, int discoveredCount, string url);
    void OnFetchCancelled(string url);
    void OnFetchError(Exception ex, string url);
    void OnCrawlCompleted(string seedUrl);

    // Redirections and scopes
    void OnOutScopeUrlReached(string url);
    void OnSeedRedirectedToNewOrigin(string currentUrl, string newOrigin);
    void OnPageRedirectedOutScope(string currentUrl, string newUrl);
    void OnPageRedirectedDisallowed(string currentUrl, string newUrl);
    void OnPageRedirectedAlreadySeen(string currentUrl, string newUrl);

    // Outcomes
    void OnPageGone(string currentUrl, string finalUrl, int statusCode);
    void OnPageFailed(string currentUrl, string finalUrl, int statusCode);
    void OnPageSkippedSize(string currentUrl, string finalUrl, long contentLength, long limit);
    void OnPageSkippedType(string currentUrl, string finalUrl, string? contentType);
    void OnPageUnchanged(string currentUrl);
    void OnPageUnchangedHash(string currentUrl, string finalUrl);
    void OnPageDuplicateContent(string currentUrl, string finalUrl, string canonicalUrl);
    void OnPageAlias(string currentUrl, string finalUrl, string canonicalUrl);
    void OnPageDisallowed(string currentUrl);
    void OnPageNoIndex(string currentUrl, string finalUrl);
    void OnPageIndexed(string currentUrl, string finalUrl, int outlinksCount);

    // Post-crawl events
    void OnStaleUrlsPruned(int count);
    void OnPruneFailed(Exception ex);
    void OnBannedUrlsRemoved(int count);
    void OnRemoveBannedFailed(Exception ex);
    void OnExternalLinksChecked(int brokenCount, int totalCount);
    void OnExternalLinkProbeFailed(Exception ex, string url);
    void OnExternalLinkBroken(string url, int statusCode);
    void OnAllowedServerIgnored(string entry);
}
