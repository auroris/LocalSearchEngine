using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// The end-of-crawl link check. It takes the links the crawl didn't already resolve — off-site links
/// (never crawled) and any in-scope links it never reached — and probes each destination with a HEAD
/// (falling back to GET), classifying it Ok, Redirect, or Error. Probes run concurrently across hosts
/// with a bounded degree of parallelism and a politeness gap between hits on the same host; the results
/// are written back to the link index, and connection failures feed the host-health tracker. A second
/// method turns the index's redirected and errored rows into the sorted broken/redirected lists the
/// report shows. Its probes are safe to run in parallel — the trackers they touch are thread-safe.
/// </summary>
internal sealed class LinkVerifier
{
    /// <summary>The maximum number of hosts to query in parallel during link checking.</summary>
    private const int LinkCheckConcurrency = 8;
    /// <summary>The polite interval/delay between link probes targeted at the same host.</summary>
    private static readonly TimeSpan LinkCheckPerHostGap = TimeSpan.FromMilliseconds(250);

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
    /// <param name="context">The active crawl context.</param>
    /// <param name="crawlStartUtc">The UTC timestamp indicating when the crawl started.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous verification process.</returns>
    public async Task VerifyUndeterminedLinksAsync(CrawlContext context, DateTime crawlStartUtc, CancellationToken cancellationToken)
    {
        var rows = await CrawlStore.GetLinksToVerifyAsync(context.Read, crawlStartUtc, CancellationToken.None);

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fromUrl, toUrl, external) in rows)
        {
            if (external && !context.CheckExternalLinks) continue;
            if (!Uri.TryCreate(fromUrl, UriKind.Absolute, out var fromUri) || !context.AllowedHosts.IsAllowed(fromUri)) continue;
            if (Uri.TryCreate(toUrl, UriKind.Absolute, out var toUri) && context.HostHealth.IsUnreachable(toUri.Host)) continue;
            destinations.Add(toUrl);
        }

        if (destinations.Count == 0) return;

        context.Observer.OnPhaseChanged(CrawlPhase.CheckingLinks);

        var byHost = destinations
             .Select(target => (Target: target, Uri: Uri.TryCreate(target, UriKind.Absolute, out var u) ? u : null))
             .Where(x => x.Uri is not null)
             .GroupBy(x => x.Uri!.Host)
             .ToList();

        var results = new ConcurrentBag<(string Target, LinkStatus Status, int StatusCode)>();
        try
        {
            await Parallel.ForEachAsync(
                byHost,
                new ParallelOptions { MaxDegreeOfParallelism = LinkCheckConcurrency, CancellationToken = cancellationToken },
                async (group, token) =>
                {
                    bool first = true;
                    foreach (var (target, _) in group)
                    {
                        if (!first) await Task.Delay(LinkCheckPerHostGap, token);
                        first = false;

                        var (status, statusCode) = await ProbeLinkAsync(target, context, token);
                        results.Add((target, status, statusCode));
                    }
                });
        }
        catch (OperationCanceledException)
        {
        }

        int broken = 0, redirected = 0;
        foreach (var (target, status, statusCode) in results)
        {
            await CrawlStore.UpdateLinkStatusByDestinationAsync(context.Write, target, (int)status, statusCode, CancellationToken.None);
            if (status == LinkStatus.Error) broken++;
            else if (status == LinkStatus.Redirect) redirected++;
        }

        context.Observer.OnLinksVerified(results.Count, broken, redirected);
    }

    /// <summary>
    /// Builds the report's broken and redirected link lists from the link index.
    /// </summary>
    /// <param name="context">The active crawl context.</param>
    /// <param name="crawlStartUtc">The UTC timestamp indicating when the crawl started.</param>
    /// <returns>A tuple containing lists of broken and redirected <see cref="BrokenLink"/> objects.</returns>
    public async Task<(List<BrokenLink> Broken, List<BrokenLink> Redirected)> BuildLinkReportAsync(CrawlContext context, DateTime crawlStartUtc)
    {
        var rows = await CrawlStore.GetReportableLinksAsync(context.Read, crawlStartUtc, CancellationToken.None);
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
    /// <param name="context">The active crawl context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the link's classified <see cref="LinkStatus"/> and actual HTTP status code.</returns>
    private async Task<(LinkStatus Status, int StatusCode)> ProbeLinkAsync(string url, CrawlContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendProbeAsync(url, cancellationToken);
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
            if (HostHealthTracker.IsTransportFailure(ex, cancellationToken))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    context.HostHealth.RecordFailure(uri.Host, ex, cancellationToken);
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
