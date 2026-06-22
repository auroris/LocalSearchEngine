namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// Shared helper for reading an HTTP response body into memory under a hard size cap, and the home of
/// the crawl's per-request timeout. The crawler streams bodies by hand with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, which <see cref="HttpClient.Timeout"/> does
/// not cover, so every HTTP call wraps itself in a <see cref="NewRequestTimeout"/> source whose token
/// bounds the whole request — headers and body alike — turning a server that stalls mid-body into a
/// logged failure instead of an indefinite hang.
/// </summary>
internal static class HttpContentReader
{
    /// <summary>The wall-clock limit for a single crawl HTTP request, covering both the response
    /// headers and the streamed body. Large files (up to the crawl size cap) must finish inside it.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Creates a fresh <see cref="CancellationTokenSource"/> that fires after <see cref="RequestTimeout"/>.
    /// This is a local per-request timeout, not crawl-wide or user cancellation; the caller disposes it.
    /// </summary>
    /// <returns>A timeout source the caller owns and should wrap in a <c>using</c>.</returns>
    public static CancellationTokenSource NewRequestTimeout() => new(RequestTimeout);

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
