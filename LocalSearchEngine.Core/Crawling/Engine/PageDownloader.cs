using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// Dictates the outcomes of a document download attempt.
/// </summary>
internal enum DownloadStatus
{
    /// <summary>The download was successful and the resource was fetched.</summary>
    Success,
    /// <summary>The server returned 304 Not Modified, indicating the local cached copy is still fresh.</summary>
    NotModified,
    /// <summary>The request was redirected to a different URL (carried in <see cref="DownloadResult.FinalRequestUri"/>); the body was not read so the caller can treat the target like a discovered link.</summary>
    Redirected,
    /// <summary>The resource returned 404 Not Found or 410 Gone.</summary>
    Gone,
    /// <summary>The connection failed, timed out, or returned an HTTP error code.</summary>
    Failed,
    /// <summary>The resource content length or downloaded body exceeded the maximum size limit.</summary>
    SizeLimitExceeded,
    /// <summary>The resource content-type is not supported for indexing.</summary>
    UnsupportedType
}

/// <summary>
/// Represents the result and metadata of a page or file download.
/// </summary>
internal sealed class DownloadResult
{
    /// <summary>Gets the outcome status of the download attempt.</summary>
    public DownloadStatus Status { get; init; }
    /// <summary>Gets the HTTP response status code.</summary>
    public HttpStatusCode StatusCode { get; init; }
    /// <summary>Gets the binary body contents of the downloaded resource, or <c>null</c> if not loaded.</summary>
    public byte[]? Body { get; init; }
    /// <summary>Gets the ETag header returned by the server, if any.</summary>
    public string? ETag { get; init; }
    /// <summary>Gets the Last-Modified header returned by the server, if any.</summary>
    public string? LastModified { get; init; }
    /// <summary>Gets the MIME type of the response content.</summary>
    public string? ContentType { get; init; }
    /// <summary>Gets the character set of the response content, if HTML/text.</summary>
    public string? CharSet { get; init; }
    /// <summary>Gets the X-Robots-Tag header value returned by the server, if any.</summary>
    public string? XRobotsTag { get; init; }
    /// <summary>Gets the final request URI after any redirects were followed.</summary>
    public Uri? FinalRequestUri { get; init; }
    /// <summary>Gets the total size in bytes read from the response stream.</summary>
    public long SizeRead { get; init; }
}

/// <summary>
/// Fetches one page or file and reduces the messy result of an HTTP GET to a single tidy
/// <see cref="DownloadResult"/> the producer can switch on. It sends conditional headers from the
/// stored ETag/Last-Modified (so an unchanged page comes back as a cheap 304) and streams the body under
/// a hard byte cap — bailing out early on an oversized Content-Length, an unsupported media type, or a
/// content signature that doesn't match. A request that ends up at a different URL is reported as a
/// redirect and returned before the body is read, leaving the producer to enqueue the target like any
/// other link. Each outcome becomes a distinct <see cref="DownloadStatus"/> (not-modified, redirected,
/// gone, too-big, unsupported, failed, or success), with transport-level failures mapped to a 503 so the
/// rest of the crawl can tell a dead connection from a real server error. It classifies; it does not
/// persist or parse — the body is handed on for extraction.
/// </summary>
internal sealed class PageDownloader
{
    /// <summary>The HTTP client instance used to execute requests.</summary>
    private readonly HttpClient _httpClient;
    /// <summary>The logger instance.</summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PageDownloader"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to send requests.</param>
    /// <param name="logger">The logger instance.</param>
    public PageDownloader(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Executes a request, validating redirects, content sizes, and mime-types.
    /// </summary>
    /// <param name="requestUri">The exact URI to put on the wire. Kept as a <see cref="Uri"/> so the
    /// href's original escaping survives — re-parsing a display-form string re-encodes it, which is
    /// how percent-encoded filesystem paths used to break.</param>
    /// <param name="normalizedUrl">The URL's normalized identity, compared against the (normalized)
    /// final request URI to detect that the request moved.</param>
    /// <param name="etag">The cached ETag, if any, for conditional request validation.</param>
    /// <param name="lastModified">The cached Last-Modified string, if any, for conditional request validation.</param>
    /// <param name="maxBytes">The maximum allowed download size in bytes.</param>
    /// <param name="acceptAnyContentType">Skips the content-type whitelist and signature sniff.
    /// Infrastructure fetches (sitemaps, feeds) are XML the page whitelist would reject; their
    /// parsers are the validation.</param>
    /// <returns>A <see cref="DownloadResult"/> object summarizing the download status and metadata.</returns>
    public async Task<DownloadResult> DownloadAsync(
        Uri requestUri,
        string normalizedUrl,
        string? etag,
        string? lastModified,
        long maxBytes,
        bool acceptAnyContentType = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.IfNoneMatch.ParseAdd(etag);
        }
        if (!string.IsNullOrEmpty(lastModified) && DateTimeOffset.TryParse(lastModified, out var lastModDate))
        {
            request.Headers.IfModifiedSince = lastModDate;
        }

        // A per-request timeout that covers the streamed body too — HttpClient.Timeout stops at the
        // headers under ResponseHeadersRead, so without this a server that goes quiet mid-body would
        // hang the single producer task indefinitely.
        using var timeout = HttpContentReader.NewRequestTimeout(_httpClient);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var finalRequestUri = response.RequestMessage?.RequestUri;

            // The request ended up somewhere else: report the redirect and hand back the target without
            // reading the body. Under ResponseHeadersRead the body hasn't been pulled yet, so the target
            // isn't downloaded here — the producer enqueues it like a discovered link and it is fetched
            // once, on its own turn. Checked before 304 so a (coincidental) conditional hit on the target
            // doesn't mask that the request moved.
            if (finalRequestUri != null
                && !string.Equals(UrlNormalizer.Normalize(finalRequestUri), normalizedUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new DownloadResult
                {
                    Status = DownloadStatus.Redirected,
                    StatusCode = response.StatusCode,
                    FinalRequestUri = finalRequestUri
                };
            }

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new DownloadResult
                {
                    Status = DownloadStatus.NotModified,
                    StatusCode = response.StatusCode,
                    FinalRequestUri = finalRequestUri
                };
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return new DownloadResult
                {
                    Status = DownloadStatus.Gone,
                    StatusCode = response.StatusCode,
                    FinalRequestUri = finalRequestUri
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new DownloadResult
                {
                    Status = DownloadStatus.Failed,
                    StatusCode = response.StatusCode,
                    FinalRequestUri = finalRequestUri
                };
            }

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > maxBytes)
            {
                return new DownloadResult
                {
                    Status = DownloadStatus.SizeLimitExceeded,
                    StatusCode = response.StatusCode,
                    FinalRequestUri = finalRequestUri,
                    SizeRead = contentLength.Value
                };
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!acceptAnyContentType && !CrawlPolicy.IsSupportedOrGenericContentType(contentType))
            {
                return new DownloadResult
                {
                    Status = DownloadStatus.UnsupportedType,
                    StatusCode = response.StatusCode,
                    FinalRequestUri = finalRequestUri,
                    ContentType = contentType
                };
            }

            var (body, truncated) = await ReadBodyAsync(response, maxBytes,
                acceptAnyContentType ? null : new PrefixCheck(contentType, finalRequestUri?.ToString() ?? normalizedUrl),
                timeout.Token);
            if (truncated)
            {
                return new DownloadResult
                {
                    Status = DownloadStatus.SizeLimitExceeded,
                    StatusCode = response.StatusCode,
                    FinalRequestUri = finalRequestUri,
                    SizeRead = maxBytes + 1
                };
            }

            if (body == null)
            {
                return new DownloadResult
                {
                    Status = DownloadStatus.UnsupportedType,
                    StatusCode = response.StatusCode,
                    FinalRequestUri = finalRequestUri,
                    ContentType = contentType
                };
            }

            string? responseEtag = response.Headers.ETag?.Tag;
            string? responseLastModified = response.Content.Headers.LastModified?.ToString("r");
            var charSet = response.Content.Headers.ContentType?.CharSet;
            response.Headers.TryGetValues("X-Robots-Tag", out var xRobotsValues);
            string? xRobotsTag = xRobotsValues != null ? string.Join(",", xRobotsValues) : null;

            _logger.LogDebug("Downloaded {Url}: {Bytes} bytes in {Ms}ms.",
                finalRequestUri?.ToString() ?? normalizedUrl, body.Length, (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

            return new DownloadResult
            {
                Status = DownloadStatus.Success,
                StatusCode = response.StatusCode,
                FinalRequestUri = finalRequestUri,
                Body = body,
                ETag = responseEtag,
                LastModified = responseLastModified,
                ContentType = contentType,
                CharSet = charSet,
                XRobotsTag = xRobotsTag
            };
        }
        catch (Exception ex)
        {
            // A fired timeout surfaces as an OperationCanceledException; surface it distinctly so a
            // stalled host is visible in the log, then fall through to the transport-failure mapping
            // (503) so the rest of the crawl treats it like any other unreachable response.
            if (timeout.IsCancellationRequested)
            {
                _logger.LogWarning("Request to {Url} timed out after {Seconds}s.", normalizedUrl, (int)_httpClient.Timeout.TotalSeconds);
            }
            return new DownloadResult
            {
                Status = DownloadStatus.Failed,
                StatusCode = HostHealthTracker.IsTransportFailure(ex) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError
            };
        }
    }

    /// <summary>The declared type and URL a body's leading bytes are verified against.</summary>
    /// <param name="ContentType">The MIME type content header value.</param>
    /// <param name="FinalUrl">The final request URL (its extension can veto a zip body).</param>
    private readonly record struct PrefixCheck(string? ContentType, string? FinalUrl);

    /// <summary>
    /// Reads the response stream up to the max byte limit, verifying the MIME type signature against
    /// the first 4096 bytes when a <see cref="PrefixCheck"/> is supplied.
    /// </summary>
    /// <param name="response">The HTTP response message containing the stream.</param>
    /// <param name="maxBytes">The maximum allowed bytes to read.</param>
    /// <param name="prefixCheck">The signature check to run, or <c>null</c> to accept any body.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the downloaded body bytes (or <c>null</c> if validation fails) and a boolean indicating if reading was truncated due to exceeding the size limit.</returns>
    private static async Task<(byte[]? Body, bool Truncated)> ReadBodyAsync(
        HttpResponseMessage response,
        long maxBytes,
        PrefixCheck? prefixCheck,
        CancellationToken cancellationToken)
    {
        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bodyStream = new MemoryStream();
        byte[] buffer = new byte[8192];
        int bytesRead;
        bool checkedPrefix = prefixCheck is null;

        while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            if (bodyStream.Length + bytesRead > maxBytes)
            {
                return (null, true);
            }

            bodyStream.Write(buffer, 0, bytesRead);

            if (!checkedPrefix && bodyStream.Length >= 4096)
            {
                checkedPrefix = true;
                var prefix = bodyStream.ToArray();
                if (!CrawlPolicy.IsSupportedPrefix(prefix, prefixCheck!.Value.ContentType, prefixCheck.Value.FinalUrl))
                {
                    return (null, false);
                }
            }
        }

        var body = bodyStream.ToArray();
        if (!checkedPrefix)
        {
            if (!CrawlPolicy.IsSupportedPrefix(body, prefixCheck!.Value.ContentType, prefixCheck.Value.FinalUrl))
            {
                return (null, false);
            }
        }

        return (body, false);
    }
}
