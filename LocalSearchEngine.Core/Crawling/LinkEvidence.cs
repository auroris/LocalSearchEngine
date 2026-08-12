namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Describes how one crawled page refers to an in-scope target. The source URL and title are supplied
/// by the crawl job; this record carries the target and the compact, editorial text surrounding one
/// link. Boilerplate and nofollow links are removed before instances are created.
/// </summary>
/// <param name="ToUrl">The normalized in-scope target URL.</param>
/// <param name="AnchorText">The link's visible label, or an accessible image/ARIA label.</param>
/// <param name="ContextText">Text from the nearest paragraph-like containing block.</param>
/// <param name="SectionHeading">The nearest preceding section heading, when present.</param>
public readonly record struct LinkEvidence(
    string ToUrl,
    string AnchorText,
    string ContextText,
    string SectionHeading);
