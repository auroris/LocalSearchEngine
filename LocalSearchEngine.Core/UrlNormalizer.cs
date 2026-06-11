namespace LocalSearchEngine.Core;

/// <summary>
/// Canonicalizes URLs so that equivalent links collapse to a single form
/// before they are queued, de-duplicated, or stored.
/// </summary>
public static class UrlNormalizer
{
    /// <summary>
    /// Strips the fragment and any trailing slash from the path. The query
    /// string is preserved. The root path ("/") is left intact.
    /// </summary>
    public static string Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (builder.Path.Length > 1 && builder.Path.EndsWith('/'))
        {
            builder.Path = builder.Path.TrimEnd('/');
        }
        return builder.Uri.ToString();
    }

    /// <summary>
    /// Parses <paramref name="url"/> as an absolute URI and normalizes it.
    /// Returns false for relative or malformed input.
    /// </summary>
    public static bool TryNormalize(string? url, out string normalized)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            normalized = Normalize(uri);
            return true;
        }
        normalized = string.Empty;
        return false;
    }
}
