namespace LocalSearchEngine.Core.Crawling.Policies;

/// <summary>
/// Tracks, per host, whether a server has answered at all this run and whether it has been written
/// off as unreachable. The rule is deliberately narrow: a host that has <em>never</em> returned an
/// HTTP response and then hits a connection-level failure (a DNS failure, a refused/reset/unreachable
/// socket, a TLS handshake failure, or a request timeout) is marked unreachable, after which the
/// crawler stops contacting it and skips its remaining URLs for the rest of the run. A host that has
/// answered even once — any status code, a 404 or 503 included — can never be written off here; its
/// later failures fall back to the normal retry-and-keep-the-index handling, because "the server
/// went quiet after talking to us" says nothing as clearly as "the server was never there".
///
/// This is per-run state, mutated only from the crawl's producer thread (like
/// <see cref="Reporting.CrawlStats"/>), so it carries no synchronization of its own.
/// </summary>
public sealed class HostHealthTracker
{
    private readonly HashSet<string> _reachable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unreachable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records that <paramref name="host"/> returned an HTTP response, marking it reachable for the
    /// rest of the run. First signal wins: a host already written off as unreachable stays that way
    /// (its requests are short-circuited, so in practice this is only ever reached for live hosts).
    /// </summary>
    /// <param name="host">The host that answered.</param>
    public void RecordContacted(string host)
    {
        if (_unreachable.Contains(host)) return;
        _reachable.Add(host);
    }

    /// <summary>
    /// Records a connection-level failure for <paramref name="host"/>. If the host has never answered
    /// this run, it is written off as unreachable.
    /// </summary>
    /// <param name="host">The host whose connection failed.</param>
    /// <returns>
    /// <c>true</c> only on the transition into the unreachable state (so the caller can log it once);
    /// <c>false</c> if the host had already answered, or was already written off.
    /// </returns>
    public bool RecordUnreachable(string host)
    {
        if (_reachable.Contains(host)) return false;
        return _unreachable.Add(host);
    }

    /// <summary>Whether <paramref name="host"/> has been written off as unreachable this run.</summary>
    /// <param name="host">The host to test.</param>
    /// <returns><c>true</c> if the host has been written off; otherwise, <c>false</c>.</returns>
    public bool IsUnreachable(string host) => _unreachable.Contains(host);

    /// <summary>The hosts written off as unreachable this run.</summary>
    public IReadOnlyCollection<string> UnreachableHosts => _unreachable;
}
