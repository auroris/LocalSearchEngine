using System;
using System.Collections.Generic;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// The composition of one crawl run: which seed sources feed the frontier and which policies shape
/// what happens after. This is where the crawler's behavior is decided — a full crawl composes
/// sitemap + root seeds with link-following and pruning on, an RSS update run composes a single feed
/// seed with following, pruning, and link verification off. Pure data; nothing here does I/O.
/// </summary>
internal sealed class CrawlPlan
{
    /// <summary>
    /// Gets the crawl's scope roots — one per configured site (a no-argument run seeds every
    /// allowed origin at once, so pruning can see the whole in-scope world in a single pass).
    /// For update runs this is the feed URL. The first entry names the run in the report.
    /// </summary>
    public required IReadOnlyList<Uri> SeedUris { get; init; }

    /// <summary>Gets the modules that seed the frontier, run in order under the pipeline's root work token.</summary>
    public required IReadOnlyList<ISeedSource> SeedSources { get; init; }

    /// <summary>Gets the host rules in crawl scope; grows mid-crawl only on a seed redirect.</summary>
    public required AllowedHosts Scope { get; init; }

    /// <summary>Gets the user-configured URL patterns whose pages are followed for links but never indexed.</summary>
    public NoIndexRules NoIndexRules { get; init; } = new();

    /// <summary>
    /// Gets a value indicating whether pages' outlinks (and stored outlinks on a 304) expand the
    /// frontier. Off for update runs: the feed names what changed, so nothing else is re-fetched.
    /// Identity enqueues (redirect targets, canonical aliases, duplicate originals) are never gated
    /// by this — dropping them would delete an item's index entry with no replacement.
    /// </summary>
    public bool FollowLinks { get; init; } = true;

    /// <summary>Gets a value indicating whether URLs the crawl no longer reaches are pruned after natural completion.
    /// Must be off for partial runs (feeds) or the prune would gut everything the run didn't visit.</summary>
    public bool PruneStale { get; init; } = true;

    /// <summary>Gets a value indicating whether the end-of-crawl link verification pass runs.
    /// Off for update runs: it probes every link not determined since crawl start, which on a
    /// partial run means nearly the whole historic link table.</summary>
    public bool VerifyLinks { get; init; } = true;

    /// <summary>Gets a value indicating whether off-site links are probed during link verification.</summary>
    public bool CheckExternalLinks { get; init; }

    /// <summary>Gets the number of concurrent crawl workers. Parallelism pays off across hosts; each single host stays sequential.</summary>
    public int CrawlWorkers { get; init; } = 4;

    /// <summary>Gets the maximum pages to index this run.</summary>
    public int MaxPages { get; init; } = int.MaxValue;

    /// <summary>Gets the maximum pages to index on any single host.</summary>
    public int MaxPagesPerHost { get; init; } = int.MaxValue;

    /// <summary>Gets the maximum size in bytes for any single download.</summary>
    public long MaxCrawlSizeBytes { get; init; } = 15 * 1024 * 1024;

    /// <summary>Gets the politeness gap between same-host fetches when robots.txt declares no crawl-delay.</summary>
    public int DefaultRequestDelayMs { get; init; } = 250;
}
