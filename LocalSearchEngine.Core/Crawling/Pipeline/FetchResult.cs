using System;
using LocalSearchEngine.Core.Crawling.Engine;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// A successful download as <see cref="Document.ProcessAsync"/> sees it. Transport outcomes
/// (redirects, 304s, errors, size/type rejections) are resolved by the worker before typed
/// processing, so a document handler only ever receives a body it can parse.
/// </summary>
/// <param name="StatusCode">The HTTP status code of the response.</param>
/// <param name="Body">The response body bytes.</param>
/// <param name="ContentType">The response media type, if declared.</param>
/// <param name="CharSet">The response charset, if declared (HTML decoding).</param>
/// <param name="ETag">The response ETag, stored for the next crawl's conditional request.</param>
/// <param name="LastModified">The response Last-Modified value, stored for the next conditional request.</param>
/// <param name="XRobotsTag">The X-Robots-Tag header value, if any.</param>
/// <param name="PriorContentHash">The content hash stored for this URL by a previous crawl, carried
/// through from the pre-fetch state read so the unchanged-content shortcut needs no second query.</param>
internal sealed record FetchResult(
    int StatusCode,
    byte[] Body,
    string? ContentType,
    string? CharSet,
    string? ETag,
    string? LastModified,
    string? XRobotsTag,
    string? PriorContentHash)
{
    /// <summary>Shapes a successful <see cref="DownloadResult"/> into what document processing consumes.</summary>
    /// <param name="download">The successful download.</param>
    /// <param name="priorContentHash">The content hash previously stored for the URL, if any.</param>
    /// <returns>The fetch result handed to <see cref="Document.ProcessAsync"/>.</returns>
    public static FetchResult FromSuccess(DownloadResult download, string? priorContentHash) => new(
        (int)download.StatusCode,
        download.Body ?? Array.Empty<byte>(),
        download.ContentType,
        download.CharSet,
        download.ETag,
        download.LastModified,
        download.XRobotsTag,
        priorContentHash);
}
