namespace LocalSearchEngine.Core.Crawling.Reporting;

/// <summary>
/// The verified state of a link in the link index: whether its destination resolved cleanly,
/// redirected, or could not be reached. Stored as the integer value in <c>LinkIndex.Status</c>,
/// so the numeric values are part of the on-disk schema and must not be reordered.
/// </summary>
public enum LinkStatus
{
    /// <summary>Not yet checked this run.</summary>
    Unknown = 0,

    /// <summary>The destination resolved successfully (a 2xx, or a 304 Not Modified).</summary>
    Ok = 1,

    /// <summary>The destination redirected: the link still works, but the source should be updated.</summary>
    Redirect = 2,

    /// <summary>The destination could not be reached (a 4xx/5xx or a connection-level failure).</summary>
    Error = 3,
}
