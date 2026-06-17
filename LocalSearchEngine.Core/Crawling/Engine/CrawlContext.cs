using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// The shared state of one crawl, passed to the producer and services so they work against the same
/// frontier and caches. It holds the queue of pending URLs and the visited set behind the
/// <see cref="Discover"/> / <see cref="EnqueueSingle"/> helpers that keep the two consistent, the
/// in-scope host rules and the per-origin robots.txt cache, the read and write database connections,
/// and the run's bookkeeping: per-host fetch timestamps and page counts, the content-hash → URL map
/// that catches duplicates, the origins whose robots.txt was unavailable, the host-health tracker, and
/// the observer that progress flows to. The crawl mutates this from the single producer task; the
/// post-crawl cleanup phases run only after the consumer has drained, so they too can touch it safely.
/// </summary>
internal sealed class CrawlContext
{
    /// <summary>Gets the scheme/host/port rules currently in crawl scope.</summary>
    public required AllowedHosts AllowedHosts { get; init; }

    /// <summary>Gets the cache of parsed robots.txt rules, keyed by origin (scheme://host:port).</summary>
    public required Dictionary<string, RobotsRules> RobotsCache { get; init; }

    /// <summary>Gets the queue of pending URLs in the frontier.</summary>
    public required Queue<string> Queue { get; init; }

    /// <summary>Gets the set of URLs already discovered and visited.</summary>
    public required HashSet<string> Visited { get; init; }

    /// <summary>Gets or sets the seed URL used to initialize the crawl.</summary>
    public string SeedUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the SQLite database connection used for read operations.</summary>
    public SqliteConnection Read { get; set; } = null!;

    /// <summary>Gets or sets the SQLite database connection used for write operations.</summary>
    public SqliteConnection Write { get; set; } = null!;

    /// <summary>Gets the lookup mapping hostnames to their last fetch timestamps.</summary>
    public Dictionary<string, DateTime> LastFetchUtc { get; } = new();

    /// <summary>Gets the lookup tracking the number of pages indexed per host.</summary>
    public Dictionary<string, int> IndexedPerHost { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the lookup mapping content hashes to the URLs they were first indexed under.</summary>
    public Dictionary<string, string> IndexedContentHashes { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the maximum size in bytes allowed for any single download (pages, files, robots.txt, sitemaps).</summary>
    public required long MaxCrawlSizeBytes { get; init; }

    /// <summary>Gets the origins (scheme://host:port) whose robots.txt was unavailable (5xx) this run; their URLs are exempt from pruning.</summary>
    public HashSet<string> RobotsUnavailable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the per-host reachability tracker: which servers have answered this run and which have been written off as unreachable.</summary>
    public HostHealthTracker HostHealth { get; } = new();

    /// <summary>Gets a value indicating whether off-site links are probed during the end-of-crawl verification pass and included in the report.</summary>
    public required bool CheckExternalLinks { get; init; }

    /// <summary>Gets or sets a value indicating whether the per-host cap skipped any URL this run, which disables pruning.</summary>
    public bool HostCapSkipped { get; set; }

    /// <summary>Gets or sets the reporter that receives live progress and phase changes.</summary>
    public required ICrawlObserver Observer { get; init; }

    /// <summary>Gets or sets the moment the crawl started, used to stamp elapsed time onto snapshots.</summary>
    public required DateTime StartedUtc { get; init; }

    /// <summary>
    /// Discovers a URL: adds to Visited and enqueues to Queue if it is new.
    /// </summary>
    /// <param name="url">The URL to discover.</param>
    /// <returns><c>true</c> if the URL was newly discovered and enqueued; otherwise, <c>false</c>.</returns>
    public bool Discover(string url)
    {
        if (!Visited.Add(url)) return false;
        Queue.Enqueue(url);
        return true;
    }

    /// <summary>
    /// Enqueues a single URL if it has not been visited yet.
    /// </summary>
    /// <param name="url">The URL to enqueue.</param>
    public void EnqueueSingle(string url)
    {
        if (Visited.Add(url))
        {
            Queue.Enqueue(url);
        }
    }
}

