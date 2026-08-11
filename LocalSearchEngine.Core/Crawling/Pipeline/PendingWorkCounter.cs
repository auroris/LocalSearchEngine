using System.Threading;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// A reference count of accepted-but-unfinished documents, the crawl's termination detector.
/// Workers both consume from and produce into the crawl channel, so "channel empty" never means
/// "crawl done" — a worker mid-page may be about to enqueue thirty more links. Instead every
/// accepted enqueue increments this counter and every fully-processed document decrements it
/// (children first, in the worker's finally), so the count strikes zero exactly once: when no
/// document is queued, in flight, or still able to spawn work. The orchestrator holds one extra
/// "root" token across seeding so a crawl whose seeds all dedup away still terminates through the
/// same path instead of hanging on a channel nobody will complete.
/// </summary>
internal sealed class PendingWorkCounter
{
    private int _pending;

    /// <summary>Counts one more accepted document (or the orchestrator's root token).</summary>
    public void Increment() => Interlocked.Increment(ref _pending);

    /// <summary>
    /// Counts one document fully processed.
    /// </summary>
    /// <returns><c>true</c> exactly once, for the decrement that struck zero — the caller must then complete the crawl channel.</returns>
    public bool Decrement() => Interlocked.Decrement(ref _pending) == 0;
}
