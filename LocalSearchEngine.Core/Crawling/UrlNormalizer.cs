namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Canonicalizes URLs so that equivalent links collapse to a single form
/// before they are queued, de-duplicated, or stored.
/// </summary>
public static class UrlNormalizer
{
    /// <summary>
    /// A set of analytics and click tracking query parameters that do not affect the page content.
    /// </summary>
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "gclid", "gclsrc", "dclid", "gbraid", "wbraid", "fbclid", "msclkid", "yclid",
        "mc_cid", "mc_eid", "igshid", "ref_src", "_hsenc", "_hsmi", "vero_id",
        "oly_enc_id", "oly_anon_id", "_openstat", "spm",
    };

    /// <summary>
    /// Canonicalizes the specified absolute URI.
    /// </summary>
    /// <param name="uri">The absolute <see cref="Uri"/> to normalize.</param>
    /// <returns>A normalized URL string with tracking parameters and fragments removed, and trailing slashes trimmed.</returns>
    public static string Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (builder.Path.Length > 1 && builder.Path.EndsWith('/'))
        {
            builder.Path = builder.Path.TrimEnd('/');
        }
        builder.Query = StripTrackingParams(uri.Query);
        return builder.Uri.ToString();
    }

    /// <summary>
    /// Strips known tracking parameters from a raw query string.
    /// </summary>
    /// <param name="query">The raw query string, with or without a leading '?'.</param>
    /// <returns>A query string with tracking parameters removed, without a leading '?'.</returns>
    private static string StripTrackingParams(string query)
    {
        if (string.IsNullOrEmpty(query)) return string.Empty;
        var body = query[0] == '?' ? query[1..] : query;
        if (body.Length == 0) return string.Empty;

        var kept = new List<string>();
        foreach (var pair in body.Split('&'))
        {
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            var key = eq >= 0 ? pair[..eq] : pair;
            if (key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || TrackingParams.Contains(key)) continue;
            kept.Add(pair);
        }
        return string.Join("&", kept);
    }



    /// <summary>
    /// Attempts to parse and normalize the specified URL string.
    /// </summary>
    /// <param name="url">The URL string to parse.</param>
    /// <param name="normalized">When this method returns, contains the normalized URL string if parsing succeeded; otherwise, an empty string.</param>
    /// <returns><c>true</c> if the URL is absolute and successfully normalized; otherwise, <c>false</c>.</returns>
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
