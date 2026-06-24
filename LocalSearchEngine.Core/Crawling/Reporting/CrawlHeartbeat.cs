namespace LocalSearchEngine.Core.Crawling.Reporting;

using System;

/// <summary>
/// Two independent "what is this half of the crawl doing right now" markers — one for the crawler thread
/// (fetch plus crawl-state writes) and one for the embedder thread (CPU embedding plus chunk writes). Each
/// thread bumps its own lane before every unit of work, and the orchestrator's watchdog reads both on a
/// timer and warns per lane when one has been in flight too long. Two lanes rather than one because the
/// halves run concurrently: a single shared marker only ever showed whichever thread wrote last, so a stuck
/// embedder was routinely masked by the crawler's next fetch mark (and a slow fetch by the embedder's), and
/// the log named the wrong half. <see cref="MarkCrawler"/>/<see cref="MarkEmbedder"/> and the reads are all
/// safe to call from the two threads at once.
/// </summary>
internal sealed class CrawlHeartbeat
{
    /// <summary>The sentinel a lane carries when its thread is parked with no work; the watchdog ignores it.</summary>
    public const string Idle = "idle";

    private readonly object _gate = new();
    private Lane _crawler = new("starting", DateTime.UtcNow);
    private Lane _embedder = new(Idle, DateTime.UtcNow);

    /// <summary>Records what the crawler thread is now doing, resetting its clock.</summary>
    /// <param name="activity">A short description (e.g. "fetching {url}"), or <see cref="Idle"/> when parked.</param>
    public void MarkCrawler(string activity) => Set(ref _crawler, activity);

    /// <summary>Records what the embedder thread is now doing, resetting its clock.</summary>
    /// <param name="activity">A short description (e.g. "embedding {url} (backlog N)"), or <see cref="Idle"/> when parked.</param>
    public void MarkEmbedder(string activity) => Set(ref _embedder, activity);

    /// <summary>Reads the crawler lane's current activity and how long it has been running.</summary>
    public (string Activity, TimeSpan Elapsed) ReadCrawler() => Get(ref _crawler);

    /// <summary>Reads the embedder lane's current activity and how long it has been running.</summary>
    public (string Activity, TimeSpan Elapsed) ReadEmbedder() => Get(ref _embedder);

    private void Set(ref Lane lane, string activity)
    {
        lock (_gate)
        {
            lane = new Lane(activity, DateTime.UtcNow);
        }
    }

    private (string Activity, TimeSpan Elapsed) Get(ref Lane lane)
    {
        lock (_gate)
        {
            return (lane.Activity, DateTime.UtcNow - lane.SinceUtc);
        }
    }

    private readonly record struct Lane(string Activity, DateTime SinceUtc);
}
