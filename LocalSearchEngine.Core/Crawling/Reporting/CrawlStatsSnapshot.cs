namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// An immutable point-in-time view of a crawl's <see cref="CrawlStats"/>, handed to reporters so a
/// live display can render from a stable copy rather than reaching into the mutable tally.
/// </summary>
/// <param name="Phase">The phase the crawl was in when the snapshot was taken.</param>
/// <param name="Discovered">Unique URLs discovered so far (the crawl frontier; the progress denominator).</param>
/// <param name="Indexed">Pages whose content was (re-)indexed (the progress numerator).</param>
/// <param name="Unchanged">Pages unchanged since the last crawl (304 or identical hash).</param>
/// <param name="NoIndex">Pages fetched but left unindexed by a noindex directive.</param>
/// <param name="SkippedType">Pages skipped for an unsupported content type or failed sniff.</param>
/// <param name="SkippedSize">Pages skipped for exceeding the size limit.</param>
/// <param name="Redirected">URLs that resolved to a redirect, canonical alias, or duplicate.</param>
/// <param name="Gone">Pages found gone (404/410) and removed from the index.</param>
/// <param name="Disallowed">URLs not fetched because robots.txt disallowed them.</param>
/// <param name="Failed">URLs that errored or returned a non-success status.</param>
/// <param name="LinksFound">Total outlinks seen across pages, duplicates included.</param>
/// <param name="RemovedBanned">Indexed URLs removed because robots.txt now disallows them.</param>
/// <param name="RemovedStale">Indexed URLs pruned because a completed crawl no longer reaches them.</param>
/// <param name="Elapsed">Wall-clock time since the crawl started.</param>
public readonly record struct CrawlStatsSnapshot(
    CrawlPhase Phase,
    int Discovered,
    int Indexed,
    int Unchanged,
    int NoIndex,
    int SkippedType,
    int SkippedSize,
    int Redirected,
    int Gone,
    int Disallowed,
    int Failed,
    long LinksFound,
    int RemovedBanned,
    int RemovedStale,
    TimeSpan Elapsed)
{
    /// <summary>Pages this crawl has finished processing (every outcome counted).</summary>
    public int Processed =>
        Indexed + Unchanged + NoIndex + SkippedType + SkippedSize + Redirected + Gone + Disallowed + Failed;

    /// <summary>Index entries removed this crawl: gone pages plus banned and stale removals.</summary>
    public int Removed => Gone + RemovedBanned + RemovedStale;
}
