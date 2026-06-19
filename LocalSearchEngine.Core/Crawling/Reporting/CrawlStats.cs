namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// Running tallies for a single crawl. Updated only from the crawl's producer thread (the loop that
/// fetches and classifies pages, and the end-of-crawl removals that run after the indexer drains),
/// so it carries no synchronization of its own.
/// </summary>
public sealed class CrawlStats
{
    /// <summary>Pages whose content was (re-)indexed.</summary>
    public int Indexed { get; private set; }

    /// <summary>Pages unchanged since the last crawl (304 or identical content hash).</summary>
    public int Unchanged { get; private set; }

    /// <summary>Pages fetched but left unindexed by a noindex directive.</summary>
    public int NoIndex { get; private set; }

    /// <summary>Pages skipped for an unsupported content type or a failed sniff.</summary>
    public int SkippedType { get; private set; }

    /// <summary>Pages skipped for exceeding the size limit.</summary>
    public int SkippedSize { get; private set; }

    /// <summary>Pages (PDFs) left unindexed because their extracted text was unusable (no text layer or a broken font encoding).</summary>
    public int LowQualityText { get; private set; }

    /// <summary>URLs that resolved to a redirect, canonical alias, or duplicate.</summary>
    public int Redirected { get; private set; }

    /// <summary>Pages found gone (404/410) and removed from the index.</summary>
    public int Gone { get; private set; }

    /// <summary>URLs not fetched because robots.txt disallowed them.</summary>
    public int Disallowed { get; private set; }

    /// <summary>URLs that errored or returned a non-success status.</summary>
    public int Failed { get; private set; }

    /// <summary>Total outlinks seen across pages, duplicates included.</summary>
    public long LinksFound { get; private set; }

    /// <summary>Indexed URLs removed because an origin's robots.txt now disallows them.</summary>
    public int RemovedBanned { get; private set; }

    /// <summary>Indexed URLs pruned because a completed crawl no longer reaches them.</summary>
    public int RemovedStale { get; private set; }

    /// <summary>Records a single page's resolved outcome.</summary>
    /// <param name="outcome">The outcome to tally.</param>
    public void Record(CrawlOutcome outcome)
    {
        switch (outcome)
        {
            case CrawlOutcome.Indexed: Indexed++; break;
            case CrawlOutcome.Unchanged: Unchanged++; break;
            case CrawlOutcome.NoIndex: NoIndex++; break;
            case CrawlOutcome.SkippedType: SkippedType++; break;
            case CrawlOutcome.SkippedSize: SkippedSize++; break;
            case CrawlOutcome.LowQualityText: LowQualityText++; break;
            case CrawlOutcome.Redirected: Redirected++; break;
            case CrawlOutcome.Gone: Gone++; break;
            case CrawlOutcome.Disallowed: Disallowed++; break;
            case CrawlOutcome.Failed: Failed++; break;
        }
    }

    /// <summary>Adds to the running total of outlinks discovered on a page.</summary>
    /// <param name="count">The number of outlinks found on the page.</param>
    public void AddLinks(int count) => LinksFound += count;

    /// <summary>Adds to the count of URLs removed for being newly robots-disallowed.</summary>
    /// <param name="count">The number removed.</param>
    public void AddRemovedBanned(int count) => RemovedBanned += count;

    /// <summary>Adds to the count of stale URLs pruned at end of crawl.</summary>
    /// <param name="count">The number pruned.</param>
    public void AddRemovedStale(int count) => RemovedStale += count;

    /// <summary>
    /// Captures an immutable snapshot, stamped with the moment's phase, discovered-URL count, and
    /// elapsed time (which the stats object does not track itself).
    /// </summary>
    /// <param name="phase">The current crawl phase.</param>
    /// <param name="discovered">The number of unique URLs discovered so far.</param>
    /// <param name="elapsed">Wall-clock time since the crawl started.</param>
    /// <returns>A snapshot of the current tallies.</returns>
    public CrawlStatsSnapshot Snapshot(CrawlPhase phase, int discovered, TimeSpan elapsed) => new(
        phase, discovered, Indexed, Unchanged, NoIndex, SkippedType, SkippedSize, LowQualityText,
        Redirected, Gone, Disallowed, Failed, LinksFound, RemovedBanned, RemovedStale, elapsed);
}
