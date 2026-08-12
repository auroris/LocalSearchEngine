using System.Collections.Generic;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// Represents the base crawl job result class containing URL and HTTP status information.
/// </summary>
/// <param name="Url">The target page URL.</param>
/// <param name="StatusCode">The response HTTP status code.</param>
internal abstract record CrawlJob(string Url, int StatusCode);

/// <summary>
/// Represents a job classification to fully index the page content and outlinks.
/// </summary>
/// <param name="Url">The target page URL.</param>
/// <param name="StatusCode">The response HTTP status code.</param>
/// <param name="Title">The document/page title.</param>
/// <param name="Headings">The extracted page headings text.</param>
/// <param name="Text">The main extracted text content.</param>
/// <param name="ETag">The HTTP response ETag for cache validation.</param>
/// <param name="LastModified">The HTTP response Last-Modified header value.</param>
/// <param name="ContentHash">The SHA256 hash of the extracted indexable content (title, headings, text).</param>
/// <param name="Outlinks">List of extracted in-scope outlinks.</param>
/// <param name="OffsiteLinks">List of extracted out-of-scope/offsite links.</param>
/// <param name="LinkEvidence">Anchor and nearby text describing the in-scope outlinks.</param>
/// <param name="DocKind">The classified document kind (Html/Pdf/Docx) of the indexed content.</param>
internal sealed record IndexJob(
    string Url, int StatusCode, string? Title, string Headings, string Text,
    string? ETag, string? LastModified, string ContentHash, IReadOnlyCollection<string> Outlinks,
    IReadOnlyCollection<string> OffsiteLinks, IReadOnlyCollection<LinkEvidence> LinkEvidence, DocKind DocKind)
    : CrawlJob(Url, StatusCode);

/// <summary>
/// Represents a job classification where indexing is skipped but crawl state and outlinks are stored.
/// </summary>
/// <param name="Url">The target page URL.</param>
/// <param name="StatusCode">The response HTTP status code.</param>
/// <param name="Title">The document/page title.</param>
/// <param name="ETag">The HTTP response ETag.</param>
/// <param name="LastModified">The HTTP response Last-Modified header value.</param>
/// <param name="ContentHash">The SHA256 hash of the extracted indexable content, or <c>null</c> when the page was not parsed for indexing.</param>
/// <param name="Outlinks">List of extracted in-scope outlinks.</param>
/// <param name="OffsiteLinks">List of extracted out-of-scope/offsite links.</param>
/// <param name="LinkEvidence">Anchor and nearby text describing the in-scope outlinks.</param>
/// <param name="DocKind">The classified document kind (Html/Pdf/Docx) of the page, recorded even though its content is not indexed.</param>
internal sealed record NoIndexJob(
    string Url, int StatusCode, string? Title, string? ETag, string? LastModified,
    string? ContentHash, IReadOnlyCollection<string> Outlinks, IReadOnlyCollection<string> OffsiteLinks,
    IReadOnlyCollection<LinkEvidence> LinkEvidence, DocKind DocKind)
    : CrawlJob(Url, StatusCode);

/// <summary>
/// Represents a job classification for pages that returned 404 or 410 Gone status.
/// </summary>
/// <param name="Url">The target page URL.</param>
/// <param name="StatusCode">The response HTTP status code.</param>
internal sealed record GoneJob(string Url, int StatusCode)
    : CrawlJob(Url, StatusCode);

/// <summary>
/// Represents a job classification for a URL that is no longer a page of its own — a redirect source,
/// a canonical alias, or duplicate content. Its content, links, and chunks are dropped; whatever it
/// points at is enqueued separately.
/// </summary>
/// <param name="Url">The target page URL.</param>
/// <param name="StatusCode">The response HTTP status code (302 for a redirect source).</param>
internal sealed record AliasJob(string Url, int StatusCode)
    : CrawlJob(Url, StatusCode);

/// <summary>
/// Represents a job classification for unchanged pages (304) or transient errors. A parsed 200
/// response may carry refreshed links even when its indexable text hash is unchanged; an HTTP 304
/// carries null collections and preserves the stored links.
/// </summary>
/// <param name="Url">The target page URL.</param>
/// <param name="StatusCode">The response HTTP status code.</param>
/// <param name="SourceTitle">The current source-page title when the response was parsed.</param>
/// <param name="Outlinks">Fresh in-scope outlinks, or <c>null</c> when the page was not parsed.</param>
/// <param name="OffsiteLinks">Fresh off-site outlinks, or <c>null</c> when the page was not parsed.</param>
/// <param name="LinkEvidence">Fresh anchor/context evidence, or <c>null</c> when the page was not parsed.</param>
internal sealed record TouchJob(
    string Url,
    int StatusCode,
    string? SourceTitle = null,
    IReadOnlyCollection<string>? Outlinks = null,
    IReadOnlyCollection<string>? OffsiteLinks = null,
    IReadOnlyCollection<LinkEvidence>? LinkEvidence = null)
    : CrawlJob(Url, StatusCode);
