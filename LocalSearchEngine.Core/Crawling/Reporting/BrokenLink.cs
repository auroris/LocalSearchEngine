namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// A link the crawl found to lead nowhere: an in-scope page that returned 404/410 while crawling,
/// or — when external link checking is enabled — an off-site link a final verification pass could
/// not resolve. Carries both ends of the link so a report can say what is broken and where it was
/// seen.
/// </summary>
/// <param name="Url">The link target that failed.</param>
/// <param name="FoundOn">The page the link was seen on, or <c>null</c> if it came from the seed or a sitemap rather than a crawled page.</param>
/// <param name="External">Whether the target is off-site (verified in the link-check phase) rather than an in-scope page hit during the crawl.</param>
/// <param name="StatusCode">The HTTP status the target returned, or <c>0</c> if the request never completed (a connection-level failure).</param>
/// <param name="Reason">A short human-readable explanation, e.g. "404 Not Found", "410 Gone", or "connection failed".</param>
public sealed record BrokenLink(
    string Url,
    string? FoundOn,
    bool External,
    int StatusCode,
    string Reason);
