using System;
using System.Collections.Concurrent;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// The crawl-wide set of URL identities already accepted into the frontier. <see cref="TryMarkSeen"/>
/// is the single atomic dedup decision: with several workers discovering the same link at once, a
/// check-then-add would let two of them both enqueue it, so membership and insertion are one
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>. Keys are normalized URLs
/// (<see cref="Policies.UrlNormalizer"/> output), compared case-insensitively like the old frontier.
/// </summary>
internal sealed class VisitedSet
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Claims a URL identity for the frontier.
    /// </summary>
    /// <param name="dedupKey">The normalized URL to claim.</param>
    /// <returns><c>true</c> exactly once per key — for the caller that won the claim; <c>false</c> for every later caller.</returns>
    public bool TryMarkSeen(string dedupKey) => _seen.TryAdd(dedupKey, 0);

    /// <summary>Gets the number of unique URLs discovered so far (the live "discovered" figure).</summary>
    public int Count => _seen.Count;
}
