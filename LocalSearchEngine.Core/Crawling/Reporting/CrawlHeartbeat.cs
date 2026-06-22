namespace LocalSearchEngine.Core.Crawling.Reporting;

using System;

/// <summary>
/// A single shared "what is the crawl doing right now" marker, updated by the producer (before each
/// fetch and on every phase change) and the consumer (before each page's writes). The orchestrator's
/// watchdog reads it on a timer and warns when one activity has been in flight too long — the signal
/// that the crawl has stalled, and the only thing in the log that says where. A genuine stall halts
/// both producer and consumer, so progress stops being marked and the stuck activity string pinpoints
/// the culprit. <see cref="Mark"/> and <see cref="Read"/> are safe to call from those two threads at once.
/// </summary>
internal sealed class CrawlHeartbeat
{
    private readonly object _gate = new();
    private string _activity = "starting";
    private DateTime _sinceUtc = DateTime.UtcNow;

    /// <summary>Records that the crawl has moved on to <paramref name="activity"/>, resetting the clock.</summary>
    /// <param name="activity">A short description of the work now in progress (e.g. "fetching {url}").</param>
    public void Mark(string activity)
    {
        lock (_gate)
        {
            _activity = activity;
            _sinceUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Reads the current activity and how long it has been running.</summary>
    /// <returns>The activity description and the elapsed time since it was last marked.</returns>
    public (string Activity, TimeSpan Elapsed) Read()
    {
        lock (_gate)
        {
            return (_activity, DateTime.UtcNow - _sinceUtc);
        }
    }
}
