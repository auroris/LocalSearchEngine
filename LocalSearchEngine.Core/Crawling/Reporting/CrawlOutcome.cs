namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// How the crawler resolved a single URL it dequeued. Each value is a decision the producer makes
/// while fetching and classifying a page, and is the unit the run statistics and the live view are
/// aggregated from.
/// </summary>
public enum CrawlOutcome
{
    /// <summary>Content was (re-)indexed.</summary>
    Indexed,

    /// <summary>Unchanged since the last crawl (HTTP 304 or an identical content hash).</summary>
    Unchanged,

    /// <summary>Fetched but not indexed by choice: a noindex directive.</summary>
    NoIndex,

    /// <summary>Fetched but skipped: unsupported content type, or a body that failed sniffing.</summary>
    SkippedType,

    /// <summary>Skipped before or during download: larger than the size limit.</summary>
    SkippedSize,

    /// <summary>Fetched but not indexed: the extracted text was unusable (no text layer, or a broken font encoding).</summary>
    LowQualityText,

    /// <summary>Resolved to another URL: a redirect, a canonical alias, or duplicate content.</summary>
    Redirected,

    /// <summary>Gone (HTTP 404/410): removed from the index.</summary>
    Gone,

    /// <summary>Not fetched: disallowed by robots.txt.</summary>
    Disallowed,

    /// <summary>A request error or non-success status; whatever was already indexed is kept.</summary>
    Failed,
}
