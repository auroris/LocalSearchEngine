using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// What the link verification pass needs from the finished crawl. Write access is only safe because
/// the pass runs after the persistence consumer has drained — it inherits the run's single-writer slot.
/// </summary>
/// <param name="Read">The orchestrator's read connection.</param>
/// <param name="Write">The orchestrator's write connection.</param>
/// <param name="Scope">The crawl's host rules; links found on out-of-scope pages are not probed.</param>
/// <param name="HostHealth">The run's reachability tracker; links on written-off hosts are skipped.</param>
/// <param name="CheckExternalLinks">Whether off-site destinations are probed at all.</param>
/// <param name="Observer">The crawl's event sink.</param>
/// <param name="Heartbeat">The activity marker bumped as probes land.</param>
internal sealed record LinkVerificationContext(
    Microsoft.Data.Sqlite.SqliteConnection Read,
    Microsoft.Data.Sqlite.SqliteConnection Write,
    AllowedHosts Scope,
    HostHealthTracker HostHealth,
    bool CheckExternalLinks,
    ICrawlObserver Observer,
    CrawlHeartbeat Heartbeat);

/// <summary>
/// The end-of-crawl link check. It takes the links the crawl didn't already resolve — off-site links
/// (never crawled) and any in-scope links it never reached — and probes each destination with a HEAD
/// (falling back to GET), classifying it Ok, Redirect, or Error. Probes run concurrently with a bounded
/// degree of parallelism; unlike the content crawl these are one-shot, read-only HEAD/GET checks, so they
/// don't take the crawl's per-host politeness delay (which, when the undetermined links pile up on a single
/// host, would drag the pass out to a near-serial trickle). Results are written back to the link index, and
/// connection failures feed the host-health tracker. A second method turns the index's redirected and
/// errored rows into the sorted broken/redirected lists the report shows. Its probes are safe to run in
/// parallel — the trackers they touch are thread-safe.
/// </summary>
internal sealed class LinkVerifier
{
    /// <summary>The maximum number of link probes to run in flight at once, across all hosts.</summary>
    private const int LinkCheckConcurrency = 16;

    /// <summary>The HTTP client instance used to probe links.</summary>
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkVerifier"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to send probe requests.</param>
    public LinkVerifier(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Verify links not already determined this run — off-site links (never crawled) plus any
    /// in-scope links the crawl didn't reach.
    /// </summary>
    /// <param name="context">The finished crawl's verification context.</param>
    /// <param name="crawlStartUtc">The UTC timestamp indicating when the crawl started.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous verification process.</returns>
    public async Task VerifyUndeterminedLinksAsync(LinkVerificationContext context, DateTime crawlStartUtc)
    {
        var rows = await CrawlStore.GetLinksToVerifyAsync(context.Read, crawlStartUtc);

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fromUrl, toUrl, external) in rows)
        {
            if (external && !context.CheckExternalLinks) continue;
            if (!Uri.TryCreate(fromUrl, UriKind.Absolute, out var fromUri) || !context.Scope.IsAllowed(fromUri)) continue;
            if (Uri.TryCreate(toUrl, UriKind.Absolute, out var toUri) && context.HostHealth.IsUnreachable(toUri.Host)) continue;
            destinations.Add(toUrl);
        }

        if (destinations.Count == 0) return;

        context.Observer.OnPhaseChanged(CrawlPhase.CheckingLinks);

        int total = destinations.Count;
        int done = 0;
        var results = new ConcurrentBag<(string Target, LinkStatus Status, int StatusCode)>();
        await Parallel.ForEachAsync(
            destinations,
            new ParallelOptions { MaxDegreeOfParallelism = LinkCheckConcurrency },
            async (target, _) =>
            {
                var (status, statusCode) = await ProbeLinkAsync(target, context);
                results.Add((target, status, statusCode));

                // Bump the crawler heartbeat as each probe lands so the watchdog sees this long post-crawl
                // pass making progress instead of reading its one-time phase mark as a multi-hour stall, and
                // so a real hang (every probe wedged) still trips it.
                context.Heartbeat.MarkCrawler($"checking links ({Interlocked.Increment(ref done)}/{total})");
            });

        int broken = 0, redirected = 0;
        foreach (var (target, status, statusCode) in results)
        {
            await CrawlStore.UpdateLinkStatusByDestinationAsync(context.Write, target, (int)status, statusCode);
            if (status == LinkStatus.Error) broken++;
            else if (status == LinkStatus.Redirect) redirected++;
        }

        context.Observer.OnLinksVerified(results.Count, broken, redirected);
    }

    /// <summary>
    /// Builds the report's broken and redirected link lists from the link index.
    /// </summary>
    /// <param name="context">The finished crawl's verification context.</param>
    /// <param name="crawlStartUtc">The UTC timestamp indicating when the crawl started.</param>
    /// <returns>A tuple containing lists of broken and redirected <see cref="BrokenLink"/> objects.</returns>
    public async Task<(List<BrokenLink> Broken, List<BrokenLink> Redirected)> BuildLinkReportAsync(LinkVerificationContext context, DateTime crawlStartUtc)
    {
        var rows = await CrawlStore.GetReportableLinksAsync(context.Read, crawlStartUtc);
        var broken = new List<BrokenLink>();
        var redirected = new List<BrokenLink>();

        foreach (var (fromUrl, toUrl, external, statusVal, statusCode) in rows)
        {
            var status = (LinkStatus)statusVal;
            if (status == LinkStatus.Error)
            {
                broken.Add(new BrokenLink(toUrl, fromUrl, external, statusCode, ReasonFor(status, statusCode)));
            }
            else if (status == LinkStatus.Redirect)
            {
                redirected.Add(new BrokenLink(toUrl, fromUrl, external, statusCode, ReasonFor(status, statusCode)));
            }
        }

        broken.Sort(CompareLinks);
        redirected.Sort(CompareLinks);

        return (broken, redirected);
    }

    /// <summary>
    /// Probes a single link to determine if it is OK, redirected, or broken.
    /// </summary>
    /// <param name="url">The link URL to probe.</param>
    /// <param name="context">The finished crawl's verification context.</param>
    /// <returns>A tuple containing the link's classified <see cref="LinkStatus"/> and actual HTTP status code.</returns>
    private async Task<(LinkStatus Status, int StatusCode)> ProbeLinkAsync(string url, LinkVerificationContext context)
    {
        using var timeout = HttpContentReader.NewRequestTimeout(_httpClient);
        try
        {
            using var response = await SendProbeAsync(url, timeout.Token);
            context.HostHealth.RecordResponse(response.RequestMessage?.RequestUri?.Host ?? "");

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri != null)
            {
                var normalizedFinal = UrlNormalizer.Normalize(finalUri);
                if (!string.Equals(normalizedFinal, url, StringComparison.OrdinalIgnoreCase))
                {
                    return (LinkStatus.Redirect, (int)response.StatusCode);
                }
            }

            int code = (int)response.StatusCode;
            if (code is >= 400 and < 600) return (LinkStatus.Error, code);
            return (LinkStatus.Ok, code);
        }
        catch (Exception ex)
        {
            // A transport failure (DNS/connect/TLS/timeout) means the destination is genuinely
            // unreachable: report it broken and feed the host-health tracker so its other links
            // are short-circuited. Any other exception proves nothing about the link itself
            // (a malformed response, an odd redirect, a client-side quirk), so don't condemn a
            // link on that evidence — treat it as resolved.
            if (HostHealthTracker.IsTransportFailure(ex))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    context.HostHealth.RecordFailure(uri.Host, ex);
                }
                return (LinkStatus.Error, 503);
            }
            return (LinkStatus.Ok, 0);
        }
    }

    /// <summary>
    /// Sends an HTTP probe request to the given URL, trying HEAD first and falling back to GET.
    /// </summary>
    /// <param name="url">The target URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The HTTP response message.</returns>
    private async Task<HttpResponseMessage> SendProbeAsync(string url, CancellationToken cancellationToken)
    {
        var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch
        {
            var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            return await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.MethodNotAllowed || (int)response.StatusCode == 400)
        {
            response.Dispose();
            var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            return await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        return response;
    }

    /// <summary>
    /// Compares two broken links for sorting (by source URL first, then target URL).
    /// </summary>
    /// <param name="a">The first link.</param>
    /// <param name="b">The second link.</param>
    /// <returns>A comparison integer.</returns>
    private static int CompareLinks(BrokenLink a, BrokenLink b)
    {
        int c = string.Compare(a.FoundOn, b.FoundOn, StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c;
        return string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a human-readable reason string based on the status classification and HTTP status code.
    /// </summary>
    /// <param name="status">The classified link status.</param>
    /// <param name="statusCode">The response HTTP status code.</param>
    /// <returns>A string description of the link status reason.</returns>
    private static string ReasonFor(LinkStatus status, int statusCode)
    {
        if (status == LinkStatus.Redirect)
        {
            return statusCode == 301 ? "301 Permanent Redirect" : "302 Temporary Redirect";
        }
        if (statusCode == 0) return "Connection Failed";
        if (statusCode == 404) return "404 Not Found";
        if (statusCode == 410) return "410 Gone";
        if (statusCode == 403) return "403 Forbidden";
        if (statusCode == 503) return "503 Service Unavailable";
        return $"HTTP {statusCode} Error";
    }
}
