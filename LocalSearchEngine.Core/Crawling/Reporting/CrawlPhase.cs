namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// The high-level stage a crawl is in. Surfaced so a host can label what the crawler is doing —
/// especially the end-of-crawl database work that happens after the last page is fetched, which
/// would otherwise look like a frozen display.
/// </summary>
public enum CrawlPhase
{
    /// <summary>Opening the database and fetching the seed's robots.txt and sitemaps.</summary>
    Starting,

    /// <summary>Draining the frontier: fetching, classifying, and indexing pages.</summary>
    Crawling,

    /// <summary>Removing already-indexed URLs an origin's robots.txt now disallows.</summary>
    RemovingBanned,

    /// <summary>Pruning indexed URLs a completed crawl no longer reaches.</summary>
    Pruning,

    /// <summary>Optimizing and (when warranted) vacuuming the database.</summary>
    Optimizing,

    /// <summary>The crawl drained its frontier and finished on its own.</summary>
    Completed,

    /// <summary>The crawl stopped early because cancellation was requested.</summary>
    Cancelled,
}
