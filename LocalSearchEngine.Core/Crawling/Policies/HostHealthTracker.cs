using System.Net.Sockets;

namespace LocalSearchEngine.Core.Crawling.Policies;

/// <summary>
/// Tracks, per host, whether a server has answered at all this run and whether it has been written
/// off as unreachable. The rule is deliberately narrow: a host that has <em>never</em> returned an
/// HTTP response and then hits a connection-level failure (a DNS failure, a refused/reset/unreachable
/// socket, a TLS handshake failure, or a per-request timeout) is marked unreachable, after which the
/// crawler stops contacting it and skips its remaining URLs for the rest of the run. A host that has
/// answered even once — any status code, a 404 or 503 included — can never be written off here; its
/// later failures fall back to the normal retry-and-keep-the-index handling, because "the server
/// went quiet after talking to us" says nothing as clearly as "the server was never there".
///
/// This is the single home for reachability decisions: every place that contacts a host (robots.txt
/// fetch, page download, end-of-crawl link check) feeds outcomes here through <see cref="RecordResponse"/>
/// and <see cref="RecordFailure"/>. All members are safe to call concurrently, because the link check
/// probes many hosts in parallel.
/// </summary>
public sealed class HostHealthTracker
{
    private readonly object _gate = new();
    private readonly HashSet<string> _reachable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unreachable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records that <paramref name="host"/> returned an HTTP response, marking it reachable for the
    /// rest of the run. First signal wins: a host already written off as unreachable stays that way
    /// (its requests are short-circuited, so in practice this is only ever reached for live hosts).
    /// </summary>
    /// <param name="host">The host that answered.</param>
    public void RecordResponse(string host)
    {
        if (string.IsNullOrEmpty(host)) return;
        lock (_gate)
        {
            if (_unreachable.Contains(host)) return;
            _reachable.Add(host);
        }
    }

    /// <summary>
    /// Records that a request to <paramref name="host"/> threw <paramref name="ex"/>. If the
    /// exception is a connection-level failure and the host has never answered this run, the host is
    /// written off as unreachable. Non-transport errors leave the tracker untouched.
    /// </summary>
    /// <param name="host">The host whose request failed.</param>
    /// <param name="ex">The exception the request threw.</param>
    /// <returns>
    /// <c>true</c> only on the transition into the unreachable state (so the caller can log it once);
    /// <c>false</c> if the failure was not a transport failure, the host had already answered, or it
    /// was already written off.
    /// </returns>
    public bool RecordFailure(string host, Exception ex)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (!IsTransportFailure(ex)) return false;
        lock (_gate)
        {
            if (_reachable.Contains(host)) return false;
            return _unreachable.Add(host);
        }
    }

    /// <summary>Whether <paramref name="host"/> has been written off as unreachable this run.</summary>
    /// <param name="host">The host to test.</param>
    /// <returns><c>true</c> if the host has been written off; otherwise, <c>false</c>.</returns>
    public bool IsUnreachable(string host)
    {
        lock (_gate)
        {
            return _unreachable.Contains(host);
        }
    }

    /// <summary>A snapshot of the hosts written off as unreachable this run.</summary>
    public IReadOnlyCollection<string> UnreachableHosts
    {
        get
        {
            lock (_gate)
            {
                return _unreachable.ToArray();
            }
        }
    }

    /// <summary>
    /// Classifies whether <paramref name="ex"/> is a connection-level failure that means the host
    /// could not be reached — a DNS/connect/TLS error, a dead socket, or a timeout. A per-request
    /// timeout surfaces as a <see cref="TaskCanceledException"/>; with no crawl-wide cancellation in
    /// play, that unambiguously means the request ran out of time, so it counts as unreachable.
    /// </summary>
    /// <param name="ex">The thrown exception to evaluate.</param>
    /// <returns><c>true</c> if the error represents an unreachable host; otherwise, <c>false</c>.</returns>
    public static bool IsTransportFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError
                                                           or HttpRequestError.ConnectionError
                                                           or HttpRequestError.SecureConnectionError }:
                case SocketException:
                case TimeoutException:
                case TaskCanceledException:
                    return true;
            }
        }
        return false;
    }
}
