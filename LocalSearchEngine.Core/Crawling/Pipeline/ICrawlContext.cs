using System;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// What a <see cref="Document"/>'s processing may touch. One instance exists per worker — a thin
/// facade over the shared pipeline plus that worker's private read connection, because a
/// <see cref="SqliteConnection"/> cannot run concurrent commands.
/// </summary>
internal interface ICrawlContext
{
    /// <summary>Gets a value indicating whether discovered links expand the frontier this run.</summary>
    bool FollowLinks { get; }

    /// <summary>Gets the host rules in crawl scope.</summary>
    AllowedHosts Scope { get; }

    /// <summary>Gets the user-configured "follow, don't index" URL patterns.</summary>
    NoIndexRules NoIndexRules { get; }

    /// <summary>Gets the live view of robots.txt rules fetched so far, keyed by origin (link filtering in HTML analysis).</summary>
    System.Collections.Generic.IReadOnlyDictionary<string, RobotsRules> RobotsRules { get; }

    /// <summary>Gets the crawl's event sink.</summary>
    ICrawlObserver Observer { get; }

    /// <summary>Gets the logger.</summary>
    ILogger Logger { get; }

    /// <summary>Gets this worker's private read connection.</summary>
    SqliteConnection Read { get; }

    /// <summary>
    /// Offers a link discovered on a page (or stored from a previous visit) to the frontier.
    /// A no-op returning <c>false</c> when <see cref="FollowLinks"/> is off — this is the one
    /// switch that keeps a feed run from turning into a crawl.
    /// </summary>
    /// <param name="fetchUri">The exact resolved link target.</param>
    /// <returns><c>true</c> if the link was new, in scope, and enqueued.</returns>
    bool Discover(Uri fetchUri);

    /// <summary>
    /// The single frontier choke point: scope check first (a URL rejected before a seed redirect
    /// widens scope must not burn its dedup key), then the atomic seen-claim, then the pending-work
    /// increment, then the channel write. Never gated by <see cref="FollowLinks"/> — seed material
    /// and identity enqueues (redirect target, canonical alias, duplicate original) always pass
    /// through. Only valid while the caller holds pending work (seeding or processing a document);
    /// that invariant is what makes the write-after-complete race impossible.
    /// </summary>
    /// <param name="document">The work item to enqueue.</param>
    /// <returns><c>true</c> if accepted; <c>false</c> if out of scope or already seen.</returns>
    bool Enqueue(Document document);

    /// <summary>
    /// Sends a finished page's classification to the persistence consumer and updates the job and
    /// embedding-backlog tallies. Index jobs must first reserve a slot through
    /// <see cref="TryAcceptIndex"/>.
    /// </summary>
    /// <param name="job">The classified outcome to persist.</param>
    void Submit(CrawlJob job);

    /// <summary>
    /// Atomically resolves same-run duplicate ownership and reserves one global index slot. Two
    /// workers indexing identical content at once resolve to one accepted index entry and one
    /// alias; distinct pages cannot exceed the run's global page cap.
    /// </summary>
    /// <param name="contentHash">The extracted-content hash.</param>
    /// <param name="url">The URL claiming it.</param>
    /// <param name="duplicateOf">Receives the accepted owner when this is a same-run duplicate;
    /// otherwise <c>null</c>, including when the cap rejected the candidate.</param>
    /// <returns><c>true</c> if the URL owns a reserved index slot; otherwise, <c>false</c>.</returns>
    bool TryAcceptIndex(string contentHash, string url, out string? duplicateOf);
}
