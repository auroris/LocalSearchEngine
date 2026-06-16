namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// One end-of-run finding about a link the crawl resolved to something other than a clean success:
/// either a broken link (its destination returned an error or could not be reached) or a redirected
/// link (it still resolves, but the source should be updated). Carries both ends of the link so a
/// report can say what happened and where the link was seen.
/// </summary>
/// <param name="Url">The link target (destination).</param>
/// <param name="FoundOn">The page the link was seen on.</param>
/// <param name="External">Whether the target is off-site (outside the crawl's allowed hosts) rather than an in-scope page.</param>
/// <param name="StatusCode">The HTTP status the target returned, or <c>0</c> if the request never completed (a connection-level failure).</param>
/// <param name="Reason">A short human-readable explanation, e.g. "404 Not Found", "410 Gone", "connection failed", or "redirect".</param>
public sealed record BrokenLink(
    string Url,
    string? FoundOn,
    bool External,
    int StatusCode,
    string Reason);
