namespace LocalSearchEngine.Core;

/// <summary>
/// Decisions about which URLs are worth fetching, independent of any network state.
/// </summary>
public static class CrawlPolicy
{
    private static readonly string[] IndexableExtensions = { ".html", ".htm", ".pdf", ".docx" };

    /// <summary>
    /// True if the URL points at content we know how to index. Extensionless
    /// routes (e.g. "/docs/intro") are assumed to be HTML pages. The extension
    /// is read from the path only, so query strings like "?v=1.2" don't fool it.
    /// </summary>
    public static bool IsIndexableExtension(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return true;
        return IndexableExtensions.Contains(ext.ToLowerInvariant());
    }
}
