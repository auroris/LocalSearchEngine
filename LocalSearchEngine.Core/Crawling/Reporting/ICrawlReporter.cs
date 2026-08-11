namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// Receives crawl progress so a host (CLI, web, tests) can present it however it likes. The crawler
/// invokes these as work happens; they must be cheap and must never throw, as the crawl does not guard
/// against a misbehaving reporter. Callbacks are concurrent: <see cref="PhaseChanged"/> and
/// <see cref="PageProcessed"/> arrive from any of the parallel crawl workers, and <see cref="EmbedProgress"/>
/// from the persistence consumer, all potentially overlapping — an implementation that keeps shared
/// display state must guard it.
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

    /// <summary>
    /// The embedder finished an item. Called from the embedder thread, including while the crawler is
    /// idle and the queued backlog drains, so the embedding bar keeps moving after the crawl itself ends.
    /// </summary>
    /// <param name="processed">Items the embedder has finished so far.</param>
    /// <param name="queued">Items queued for embedding so far (the bar's denominator; still growing mid-crawl).</param>
    void EmbedProgress(int processed, int queued);
}
