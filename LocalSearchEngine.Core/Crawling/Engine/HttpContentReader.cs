namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// Shared helper for reading an HTTP response body into memory under a hard size cap, and the home of
/// the crawl's per-request timeout. The crawler streams bodies by hand with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, which <see cref="HttpClient.Timeout"/> does
/// not cover past the headers, so every HTTP call wraps itself in a <see cref="NewRequestTimeout"/>
/// source whose token bounds the whole request — headers and body alike — turning a server that
/// stalls mid-body into a logged failure instead of an indefinite hang.
/// </summary>
internal static class HttpContentReader
{
    /// <summary>
    /// Creates a fresh <see cref="CancellationTokenSource"/> that fires after <paramref name="client"/>'s
    /// own <see cref="HttpClient.Timeout"/>, extending that single configured limit (the
    /// <c>request-timeout-seconds</c> setting) over the streamed body it doesn't otherwise cover.
    /// The wall clock spans the whole request — headers, retries, and body; large files (up to the
    /// crawl size cap) must finish inside it. This is a local per-request timeout, not crawl-wide or
    /// user cancellation; the caller disposes it.
    /// </summary>
    /// <param name="client">The client the request will be sent on; its timeout is mirrored.</param>
    /// <returns>A timeout source the caller owns and should wrap in a <c>using</c>.</returns>
    public static CancellationTokenSource NewRequestTimeout(HttpClient client) => new(client.Timeout);

    /// <summary>
    /// Reads the response stream up to <paramref name="maxBytes"/>, returning the bytes read and
    /// whether the stream still had data past the limit (i.e. the body was truncated).
    /// </summary>
    /// <param name="response">The HTTP response whose content stream is read.</param>
    /// <param name="maxBytes">The maximum number of bytes to read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple of the bytes read and a flag indicating the body exceeded the limit.</returns>
    public static async Task<(byte[] Body, bool Truncated)> ReadLimitedAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bodyStream = new MemoryStream();
        var buffer = new byte[8192];
        while (bodyStream.Length < maxBytes)
        {
            int toRead = (int)Math.Min(buffer.Length, maxBytes - bodyStream.Length);
            int bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (bytesRead == 0) return (bodyStream.ToArray(), false);
            bodyStream.Write(buffer, 0, bytesRead);
        }
        bool truncated = await responseStream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) > 0;
        return (bodyStream.ToArray(), truncated);
    }
}
