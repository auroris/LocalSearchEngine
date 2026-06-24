using System;
using System.Threading;
using System.Threading.Tasks;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// Serializes the crawl's database writes down to one at a time. The crawl runs two writer threads — the
/// crawler (CrawlState/LinkIndex rows) and the embedder (text_chunks and their companion vector rows) —
/// but SQLite in WAL mode allows only a single writer at a time across connections; a second concurrent
/// writer fails with SQLITE_BUSY. Both threads take this gate around their writes so they never collide.
/// The expensive part of embedding is the CPU work, which runs <em>outside</em> the gate, so the crawler
/// almost never blocks on it — it waits only for the embedder's short upsert, not its long inference.
/// </summary>
internal sealed class DbWriteGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Waits for exclusive write access. Dispose the returned handle (a <c>using</c> block) to release it.
    /// </summary>
    /// <param name="cancellationToken">A token to abandon the wait.</param>
    /// <returns>A handle that frees the gate when disposed.</returns>
    public async ValueTask<Releaser> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    /// <summary>Releases the underlying semaphore.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>The release handle returned by <see cref="AcquireAsync"/>; disposing it frees the gate.</summary>
    public readonly struct Releaser : IDisposable
    {
        private readonly SemaphoreSlim _gate;

        internal Releaser(SemaphoreSlim gate) => _gate = gate;

        /// <summary>Frees the gate for the next writer.</summary>
        public void Dispose() => _gate.Release();
    }
}
