using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// Per-host serialization and politeness for the parallel crawl workers. Entering a host's lane
/// waits for its turn (one fetch at a time per host), then waits out the minimum gap since the
/// previous fetch started. The releaser is held through the fetch <em>and</em> the document's
/// processing, which makes each host's documents exactly sequential — that sequencing is what keeps
/// the per-host page cap exact and the politeness contract identical to the old single-threaded
/// engine. Parallelism therefore pays off across hosts, never against one.
/// </summary>
internal sealed class HostGate
{
    /// <summary>The longest crawl-delay honored from robots.txt, so a hostile value can't park the crawl.</summary>
    private const int MaxCrawlDelaySeconds = 30;

    private sealed class HostLane
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        /// <summary>Start of the previous fetch; the politeness gap is measured start-to-start. Guarded by <see cref="Turn"/>.</summary>
        public DateTime LastFetchUtc;
    }

    private readonly ConcurrentDictionary<string, HostLane> _lanes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Takes the host's turn, waiting out the politeness gap, and stamps the new fetch start.
    /// </summary>
    /// <param name="host">The host about to be fetched.</param>
    /// <param name="minGap">The minimum time between fetch starts on this host.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A releaser the caller disposes after the fetch and its processing complete.</returns>
    public async ValueTask<Releaser> EnterAsync(string host, TimeSpan minGap, CancellationToken ct = default)
    {
        var lane = _lanes.GetOrAdd(host, _ => new HostLane());
        await lane.Turn.WaitAsync(ct);
        try
        {
            var sinceLast = DateTime.UtcNow - lane.LastFetchUtc;
            if (sinceLast < minGap)
            {
                await Task.Delay(minGap - sinceLast, ct);
            }
            lane.LastFetchUtc = DateTime.UtcNow;
        }
        catch
        {
            lane.Turn.Release();
            throw;
        }
        return new Releaser(lane.Turn);
    }

    /// <summary>Releases a host's lane when disposed.</summary>
    public readonly struct Releaser : IDisposable
    {
        private readonly SemaphoreSlim _turn;
        internal Releaser(SemaphoreSlim turn) => _turn = turn;
        public void Dispose() => _turn.Release();
    }

    /// <summary>
    /// Resolves the politeness gap for a host: its robots.txt crawl-delay capped at
    /// <see cref="MaxCrawlDelaySeconds"/>, or the configured default when none is declared.
    /// </summary>
    /// <param name="robots">The host's robots.txt rules.</param>
    /// <param name="defaultRequestDelayMs">The gap to use when robots.txt declares no crawl-delay.</param>
    /// <returns>The minimum time between fetch starts on the host.</returns>
    public static TimeSpan ResolveDelay(RobotsRules robots, int defaultRequestDelayMs)
    {
        if (!robots.CrawlDelaySeconds.HasValue)
        {
            return TimeSpan.FromMilliseconds(defaultRequestDelayMs);
        }
        var delay = robots.CrawlDelaySeconds.Value;
        return TimeSpan.FromSeconds(Math.Min(delay, MaxCrawlDelaySeconds));
    }
}
