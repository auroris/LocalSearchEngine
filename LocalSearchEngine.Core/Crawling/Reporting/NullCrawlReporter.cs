namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// An <see cref="ICrawlReporter"/> that ignores every callback. Used when a crawl runs without a
/// reporter (the web app, the tests), so the crawler never has to null-check.
/// </summary>
public sealed class NullCrawlReporter : ICrawlReporter
{
    /// <summary>The shared, stateless instance.</summary>
    public static readonly NullCrawlReporter Instance = new();

    private NullCrawlReporter() { }

    /// <inheritdoc/>
    public void PhaseChanged(CrawlPhase phase, CrawlStatsSnapshot stats) { }

    /// <inheritdoc/>
    public void PageProcessed(string url, CrawlOutcome outcome, CrawlStatsSnapshot stats) { }

    /// <inheritdoc/>
    public void EmbedProgress(int processed, int queued) { }
}
