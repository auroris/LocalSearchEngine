using System.Threading;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// The embedder's running counts, shared across the two threads: the crawler bumps <see cref="Queued"/>
/// as it drops chunk work on the queue, and the embedder bumps <see cref="Processed"/> as it drains it.
/// <see cref="Pending"/> (the gap between them) drives the heartbeat's backlog figure, the two totals
/// drive the live "embedding" progress bar and the end-of-run stat, and a separate counter is used rather
/// than the channel's own <c>Count</c> because the single-reader unbounded channel does not support it.
/// All members are safe to call from both threads at once.
/// </summary>
internal sealed class EmbeddingBacklog
{
    private int _queued;
    private int _processed;

    /// <summary>Records that the crawler has queued one item for embedding.</summary>
    public void RecordQueued() => Interlocked.Increment(ref _queued);

    /// <summary>Records that the embedder has finished one item.</summary>
    public void RecordProcessed() => Interlocked.Increment(ref _processed);

    /// <summary>Total items the crawler has queued for embedding so far.</summary>
    public int Queued => Volatile.Read(ref _queued);

    /// <summary>Total items the embedder has finished so far.</summary>
    public int Processed => Volatile.Read(ref _processed);

    /// <summary>Items queued but not yet finished — the embedder's outstanding backlog.</summary>
    public int Pending => Queued - Processed;
}
