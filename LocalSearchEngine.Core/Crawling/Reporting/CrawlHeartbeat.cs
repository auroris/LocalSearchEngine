namespace LocalSearchEngine.Core.Crawling.Reporting;

using System;
using System.Collections.Generic;

/// <summary>
/// A per-lane "what is this part of the crawl doing right now" marker. Each concurrent actor
/// (crawl worker, persistence consumer) bumps its own named lane before every unit of work, and
/// the orchestrator's watchdog snapshots all lanes on a timer and warns per lane when one has
/// been in flight too long. One lane per actor rather than one shared marker because the actors
/// run concurrently: a single marker only ever showed whichever actor wrote last, so a stuck
/// one was routinely masked by another's next mark and the log named the wrong culprit.
/// All members are safe to call from any number of actors at once.
/// </summary>
internal sealed class CrawlHeartbeat
{
    /// <summary>The sentinel a lane carries when its actor is parked with no work; the watchdog ignores it.</summary>
    public const string Idle = "idle";

    /// <summary>
    /// Prefix for a lane waiting its turn at a host's politeness gate. A long wait here is queueing
    /// behind a slow or crawl-delayed host, not a stall, so the watchdog ignores it like <see cref="Idle"/> —
    /// but the marked activity should still name the host so a snapshot shows where the queue is.
    /// </summary>
    public const string PoliteWaitPrefix = "polite wait";

    private readonly object _gate = new();
    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.Ordinal);

    /// <summary>Records what the named lane's actor is now doing, resetting its clock. Lanes are created on first mark.</summary>
    /// <param name="lane">The lane name, e.g. "worker-1" or "persistence".</param>
    /// <param name="activity">A short description (e.g. "fetching {url}"), or <see cref="Idle"/> when parked.</param>
    public void Mark(string lane, string activity)
    {
        lock (_gate)
        {
            _lanes[lane] = new Lane(activity, DateTime.UtcNow);
        }
    }

    /// <summary>Records what the crawler is now doing. Shorthand for the old engine's fixed "crawler" lane.</summary>
    /// <param name="activity">A short description, or <see cref="Idle"/> when parked.</param>
    public void MarkCrawler(string activity) => Mark("crawler", activity);

    /// <summary>Records what the embedder is now doing. Shorthand for the old engine's fixed "embedder" lane.</summary>
    /// <param name="activity">A short description, or <see cref="Idle"/> when parked.</param>
    public void MarkEmbedder(string activity) => Mark("embedder", activity);

    /// <summary>Reads every lane's current activity and how long it has been running.</summary>
    /// <returns>A point-in-time copy; mutating actors never see or affect it.</returns>
    public IReadOnlyList<(string Lane, string Activity, TimeSpan Elapsed)> Snapshot()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var result = new List<(string, string, TimeSpan)>(_lanes.Count);
            foreach (var (name, lane) in _lanes)
            {
                result.Add((name, lane.Activity, now - lane.SinceUtc));
            }
            return result;
        }
    }

    /// <summary>
    /// Whether an activity is one the stall watchdog should ignore: a parked actor
    /// (<see cref="Idle"/>) or one queued at a politeness gate (<see cref="PoliteWaitPrefix"/>).
    /// </summary>
    /// <param name="activity">The lane activity to classify.</param>
    /// <returns><c>true</c> if the activity is expected to sit for long stretches; otherwise, <c>false</c>.</returns>
    public static bool IsQuiet(string activity) =>
        activity == Idle || activity.StartsWith(PoliteWaitPrefix, StringComparison.Ordinal);

    private readonly record struct Lane(string Activity, DateTime SinceUtc);
}
