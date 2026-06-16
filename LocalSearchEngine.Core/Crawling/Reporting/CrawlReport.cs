namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// The end-of-run summary returned by <c>CrawlerService.CrawlAsync</c>: the final statistics plus
/// database totals, ready to be written to a stats file or printed as a summary.
/// </summary>
/// <param name="SeedUrl">The URL the crawl started from.</param>
/// <param name="StartedUtc">When the crawl began.</param>
/// <param name="FinishedUtc">When the crawl finished.</param>
/// <param name="CompletedNaturally">Whether the crawl drained its frontier (vs. capped or cancelled).</param>
/// <param name="Cancelled">Whether cancellation was requested before the crawl finished.</param>
/// <param name="Stats">The final running tallies.</param>
/// <param name="IndexedUrlsInDb">Distinct indexed URLs in the database after the crawl.</param>
/// <param name="CrawlStateRowsInDb">Crawl-state rows in the database after the crawl.</param>
/// <param name="ItemsAdded">Net new indexed URLs this run (end total minus start total, plus deletions).</param>
/// <param name="ItemsDeleted">Index entries removed this run (gone, banned, and stale).</param>
/// <param name="BrokenLinks">Links the crawl found leading nowhere this run: a destination that returned an error (4xx/5xx) or could not be reached. Off-site links are included only when external link checking was enabled.</param>
/// <param name="RedirectedLinks">Links whose destination redirected this run: they still resolve, but the source page should be updated to point at the new location. Off-site links are included only when external link checking was enabled.</param>
/// <param name="UnreachableHosts">Hosts written off as unreachable this run (a connection-level failure with no prior response); their URLs were skipped.</param>
public sealed record CrawlReport(
    string SeedUrl,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    bool CompletedNaturally,
    bool Cancelled,
    CrawlStatsSnapshot Stats,
    long IndexedUrlsInDb,
    long CrawlStateRowsInDb,
    long ItemsAdded,
    long ItemsDeleted,
    IReadOnlyList<BrokenLink> BrokenLinks,
    IReadOnlyList<BrokenLink> RedirectedLinks,
    IReadOnlyList<string> UnreachableHosts)
{
    /// <summary>Wall-clock duration of the crawl.</summary>
    public TimeSpan Duration => FinishedUtc - StartedUtc;
}
