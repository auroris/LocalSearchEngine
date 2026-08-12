namespace LocalSearchEngine.Core.Crawling.Policies;

/// <summary>
/// The crawl's politeness clock: per host, the moment the server may next be bothered. A worker
/// claims a turn before every request; a host that hasn't been contacted within the minimum gap
/// admits immediately, otherwise the worker waits out just the remainder. Each claim moves the
/// host's next turn forward by one gap, so any number of workers can share a host and their
/// request starts still can't bunch tighter than the gap — simultaneous claims come out spaced.
///
/// Politeness bounds the request rate and nothing else: no lane or lock is held while a body
/// downloads or a document processes, so time spent on that work counts toward the next gap
/// instead of adding to it. Consequently same-host work is no longer sequential — anything that
/// needs same-host exactness (the per-host page cap) must enforce it atomically itself.
/// </summary>
internal sealed class HostPoliteness
{
    /// <summary>The longest crawl-delay honored from robots.txt, so a hostile value can't park the crawl.</summary>
    private const int MaxCrawlDelaySeconds = 30;

    private readonly object _gate = new();

    /// <summary>Per host, the earliest <see cref="Environment.TickCount64"/> at which the next request may start. Guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, long> _nextTurnAtMs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Claims the next polite moment to contact <paramref name="host"/> and waits until it arrives —
    /// which is right now whenever the host was last bothered at least <paramref name="minGap"/> ago.
    /// Gaps are measured start-to-start. A cancelled wait leaves its claim spent, which errs on the
    /// polite side.
    /// </summary>
    /// <param name="host">The host about to be contacted.</param>
    /// <param name="minGap">The minimum time between request starts on this host.</param>
    /// <param name="ct">Cancels the wait.</param>
    public ValueTask WaitTurnAsync(string host, TimeSpan minGap, CancellationToken ct = default)
    {
        long waitMs;
        lock (_gate)
        {
            // The tick clock rather than wall time: a clock adjustment mid-crawl must neither stall
            // a host for hours nor let a burst through early.
            long now = Environment.TickCount64;
            long turn = _nextTurnAtMs.TryGetValue(host, out var next) && next > now ? next : now;
            _nextTurnAtMs[host] = turn + (long)minGap.TotalMilliseconds;
            waitMs = turn - now;
        }
        return waitMs <= 0
            ? ValueTask.CompletedTask
            : new ValueTask(Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct));
    }

    /// <summary>
    /// Resolves the politeness gap for a host: its robots.txt crawl-delay capped at
    /// <see cref="MaxCrawlDelaySeconds"/>, or the configured default when none is declared.
    /// </summary>
    /// <param name="robots">The host's robots.txt rules.</param>
    /// <param name="defaultRequestDelayMs">The gap to use when robots.txt declares no crawl-delay.</param>
    /// <returns>The minimum time between request starts on the host.</returns>
    public static TimeSpan ResolveDelay(RobotsRules robots, int defaultRequestDelayMs)
    {
        if (!robots.CrawlDelaySeconds.HasValue)
        {
            return TimeSpan.FromMilliseconds(defaultRequestDelayMs);
        }
        return TimeSpan.FromSeconds(Math.Min(robots.CrawlDelaySeconds.Value, MaxCrawlDelaySeconds));
    }
}
