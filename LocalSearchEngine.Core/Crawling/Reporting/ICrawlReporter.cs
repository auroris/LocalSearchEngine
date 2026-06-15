namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// Receives crawl progress so a host (CLI, web, tests) can present it however it likes. The crawler
/// invokes these as work happens; for a single crawl every call arrives on the crawler's own
/// producer thread, so implementations need not be thread-safe — but they must be cheap and must
/// never throw, as the crawl does not guard against a misbehaving reporter.
/// </summary>
public interface ICrawlReporter
{
    /// <summary>The crawl moved into a new high-level <see cref="CrawlPhase"/>.</summary>
    /// <param name="phase">The phase just entered.</param>
    /// <param name="stats">The running tally at this moment.</param>
    void PhaseChanged(CrawlPhase phase, CrawlStatsSnapshot stats);

    /// <summary>A dequeued URL was resolved to an outcome.</summary>
    /// <param name="url">The URL that was processed.</param>
    /// <param name="outcome">How the crawler resolved it.</param>
    /// <param name="stats">The running tally, including this page.</param>
    void PageProcessed(string url, CrawlOutcome outcome, CrawlStatsSnapshot stats);
}
