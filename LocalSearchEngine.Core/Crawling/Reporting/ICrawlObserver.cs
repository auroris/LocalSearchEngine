namespace LocalSearchEngine.Core.Crawling.Reporting;

using System;
using Microsoft.Extensions.Logging;

/// <summary>
/// A stateful, per-crawl observer that centralizes all logging, statistics gathering, and progress
/// reporting. Implementations translate crawler domain events into log messages, stat increments,
/// and <see cref="ICrawlReporter"/> callbacks. Many callbacks pass both the URL originally dequeued
/// (<c>currentUrl</c>) and where it ended up after redirects/normalization (<c>finalUrl</c>); these
/// differ only when a redirect was followed.
/// </summary>
public interface ICrawlObserver
{
    /// <summary>Gets the running tallies for the crawl.</summary>
    CrawlStats Stats { get; }

    // Phase management

    /// <summary>The crawl moved into a new high-level <see cref="CrawlPhase"/>.</summary>
    void OnPhaseChanged(CrawlPhase phase);

    // Frontier tracking

    /// <summary>Outlinks found on a page were added to the frontier; folds them into the link tally.</summary>
    /// <param name="count">The number of outlinks discovered on the page.</param>
    void OnOutlinksAdded(int count);

    // Initial/High-level events

    /// <summary>The seed URL could not be parsed as an absolute URI, so the crawl cannot begin.</summary>
    void OnSeedInvalid(string seedUrl);

    /// <summary>The seed host failed to answer on first contact; there is nothing to crawl.</summary>
    void OnSeedUnreachable(string host);

    /// <summary>The seed URL itself is disallowed by robots.txt.</summary>
    void OnSeedDisallowed(string url);

    /// <summary>Cancellation was observed in the main crawl loop.</summary>
    /// <param name="dispatchedCount">Pages already dispatched for indexing when cancellation was seen.</param>
    void OnCrawlCancelled(int dispatchedCount);

    /// <summary>A host reached its per-host page cap, so the current URL is skipped.</summary>
    /// <param name="cap">The configured per-host page limit.</param>
    /// <param name="host">The host that hit the cap.</param>
    /// <param name="url">The URL skipped because of it.</param>
    void OnHostCapReached(int cap, string host, string url);

    /// <summary>A URL is about to be fetched from the frontier.</summary>
    /// <param name="indexedCount">Pages indexed so far this run.</param>
    /// <param name="discoveredCount">Unique URLs discovered so far (the frontier size).</param>
    /// <param name="url">The URL being fetched.</param>
    void OnPageFetching(int indexedCount, int discoveredCount, string url);

    /// <summary>Cancellation interrupted the fetch of this URL.</summary>
    void OnFetchCancelled(string url);

    /// <summary>An unexpected exception was thrown while fetching the URL; it is recorded as a failure.</summary>
    void OnFetchError(Exception ex, string url);

    /// <summary>The crawl finished (whether it drained the frontier or was cancelled).</summary>
    void OnCrawlCompleted(string seedUrl);

    // Redirections and scopes

    /// <summary>An out-of-scope URL reached the frontier — a guard that should not normally fire.</summary>
    void OnOutScopeUrlReached(string url);

    /// <summary>The seed redirected to a different origin, which is added to the allowed hosts so the crawl can follow it.</summary>
    /// <param name="currentUrl">The seed URL that redirected.</param>
    /// <param name="newOrigin">The new origin (scheme://host:port) now in scope.</param>
    void OnSeedRedirectedToNewOrigin(string currentUrl, string newOrigin);

    /// <summary>A non-seed page redirected outside the allowed hosts; the target is not followed.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="newUrl">The off-scope redirect target.</param>
    void OnPageRedirectedOutScope(string currentUrl, string newUrl);

    /// <summary>A redirect target is disallowed by robots.txt, so it is not followed.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="newUrl">The disallowed redirect target.</param>
    void OnPageRedirectedDisallowed(string currentUrl, string newUrl);

    /// <summary>A redirect resolved to a URL already seen this run, so it is not re-crawled.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="newUrl">The already-seen redirect target.</param>
    void OnPageRedirectedAlreadySeen(string currentUrl, string newUrl);

    // Outcomes

    /// <summary>A page returned 404/410 and is removed from the index.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="finalUrl">The URL after redirects.</param>
    /// <param name="statusCode">The HTTP status returned.</param>
    void OnPageGone(string currentUrl, string finalUrl, int statusCode);

    /// <summary>A page returned an error or non-success status; any existing index entry is kept.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="finalUrl">The URL after redirects.</param>
    /// <param name="statusCode">The HTTP status returned.</param>
    void OnPageFailed(string currentUrl, string finalUrl, int statusCode);

    /// <summary>A page or file was skipped for exceeding the size limit.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="finalUrl">The URL after redirects.</param>
    /// <param name="contentLength">The size seen, in bytes.</param>
    /// <param name="limit">The configured maximum size, in bytes.</param>
    void OnPageSkippedSize(string currentUrl, string finalUrl, long contentLength, long limit);

    /// <summary>A page or file was skipped for an unsupported content type or a failed sniff.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="finalUrl">The URL after redirects.</param>
    /// <param name="contentType">The Content-Type that was rejected, if any.</param>
    void OnPageSkippedType(string currentUrl, string finalUrl, string? contentType);

    /// <summary>A page was unchanged since the last crawl (server returned HTTP 304).</summary>
    void OnPageUnchanged(string currentUrl);

    /// <summary>A page's content hash matched the stored copy, so it was not re-indexed.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="finalUrl">The URL after redirects.</param>
    void OnPageUnchangedHash(string currentUrl, string finalUrl);

    /// <summary>A page's content duplicates an already-indexed URL; the original is enqueued instead of indexing a copy.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="finalUrl">The URL after redirects.</param>
    /// <param name="canonicalUrl">The already-indexed URL with identical content.</param>
    void OnPageDuplicateContent(string currentUrl, string finalUrl, string canonicalUrl);

    /// <summary>A page declares a <c>rel="canonical"</c> pointing elsewhere in scope; the canonical is enqueued.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="finalUrl">The URL after redirects.</param>
    /// <param name="canonicalUrl">The canonical URL the page points at.</param>
    void OnPageAlias(string currentUrl, string finalUrl, string canonicalUrl);

    /// <summary>A URL was not fetched because robots.txt disallows it.</summary>
    void OnPageDisallowed(string currentUrl);

    /// <summary>A page carries a noindex directive: its content is not indexed, though its links are still followed.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="finalUrl">The URL after redirects.</param>
    void OnPageNoIndex(string currentUrl, string finalUrl);

    /// <summary>A page's content was indexed.</summary>
    /// <param name="currentUrl">The requested URL.</param>
    /// <param name="finalUrl">The URL after redirects.</param>
    /// <param name="outlinksCount">The number of in-scope outlinks found on the page.</param>
    void OnPageIndexed(string currentUrl, string finalUrl, int outlinksCount);

    // Post-crawl events

    /// <summary>Post-crawl pruning removed indexed URLs that a completed crawl no longer reaches.</summary>
    /// <param name="count">The number of stale URLs pruned.</param>
    void OnStaleUrlsPruned(int count);

    /// <summary>The stale-URL pruning pass failed.</summary>
    void OnPruneFailed(Exception ex);

    /// <summary>Post-crawl cleanup removed already-indexed URLs an origin's robots.txt now disallows.</summary>
    /// <param name="count">The number of newly banned URLs removed.</param>
    void OnBannedUrlsRemoved(int count);

    /// <summary>The robots-banned removal pass failed.</summary>
    void OnRemoveBannedFailed(Exception ex);

    /// <summary>The end-of-crawl link verification pass finished.</summary>
    /// <param name="probed">The number of previously undetermined links probed.</param>
    /// <param name="broken">How many of those resolved as broken.</param>
    /// <param name="redirected">How many of those resolved as redirected.</param>
    void OnLinksVerified(int probed, int broken, int redirected);

    /// <summary>A single link probe failed in a way that proves nothing about the target, so it is treated as resolved.</summary>
    void OnLinkProbeInconclusive(Exception ex, string url);

    /// <summary>A configured allowed-server entry could not be parsed and was ignored.</summary>
    /// <param name="entry">The malformed entry, expected as <c>[scheme://]host[:port]</c>.</param>
    void OnAllowedServerIgnored(string entry);
}
