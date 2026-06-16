using System.Collections.Generic;

namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Represents the base crawl job result class containing URL and HTTP status information.
/// </summary>
/// <param name="Url">The target page URL.</param>
/// <param name="StatusCode">The response HTTP status code.</param>
/// <param name="RedirectSourceUrl">The source URL if this job was reached via redirect.</param>
internal abstract record CrawlJob(string Url, int StatusCode, string? RedirectSourceUrl = null);

/// <summary>
/// Represents a job classification to fully index the page content and outlinks.
/// </summary>
internal sealed record IndexJob(
    string Url, int StatusCode, string? Title, string Headings, string Text,
    string? ETag, string? LastModified, string ContentHash, IReadOnlyCollection<string> Outlinks,
    IReadOnlyCollection<string> OffsiteLinks, string? RedirectSourceUrl = null)
    : CrawlJob(Url, StatusCode, RedirectSourceUrl);

/// <summary>
/// Represents a job classification where indexing is skipped but crawl state and outlinks are stored.
/// </summary>
internal sealed record NoIndexJob(
    string Url, int StatusCode, string? Title, string? ETag, string? LastModified,
    string ContentHash, IReadOnlyCollection<string> Outlinks, IReadOnlyCollection<string> OffsiteLinks,
    string? RedirectSourceUrl = null)
    : CrawlJob(Url, StatusCode, RedirectSourceUrl);

/// <summary>
/// Represents a job classification for pages that returned 404 or 410 Gone status.
/// </summary>
internal sealed record GoneJob(string Url, int StatusCode, string? RedirectSourceUrl = null)
    : CrawlJob(Url, StatusCode, RedirectSourceUrl);

/// <summary>
/// Represents a job classification for canonical page aliases.
/// </summary>
internal sealed record AliasJob(string Url, int StatusCode, string? RedirectSourceUrl = null)
    : CrawlJob(Url, StatusCode, RedirectSourceUrl);

/// <summary>
/// Represents a job classification for unchanged pages (304) or transient errors.
/// </summary>
internal sealed record TouchJob(string Url, int StatusCode, string? RedirectSourceUrl = null)
    : CrawlJob(Url, StatusCode, RedirectSourceUrl);
