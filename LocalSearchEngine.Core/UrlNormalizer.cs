namespace LocalSearchEngine.Core;

/// <summary>
/// Canonicalizes URLs so that equivalent links collapse to a single form
/// before they are queued, de-duplicated, or stored.
/// </summary>
public static class UrlNormalizer
{
    /// <summary>
    /// Analytics/click parameters that never change the content served, so two URLs
    /// differing only by these are the same page. <c>utm_*</c> is handled by prefix.
    /// </summary>
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "gclid", "gclsrc", "dclid", "gbraid", "wbraid", "fbclid", "msclkid", "yclid",
        "mc_cid", "mc_eid", "igshid", "ref_src", "_hsenc", "_hsmi", "vero_id",
        "oly_enc_id", "oly_anon_id", "_openstat", "yclid", "spm",
    };

    /// <summary>
    /// Strips the fragment, any trailing slash from the path, and known tracking
    /// parameters from the query. The remaining query is preserved verbatim and in
    /// order. The root path ("/") is left intact.
    /// </summary>
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
    /// Removes tracking parameters from a raw query string (with or without the leading
    /// '?'), returning the cleaned query without a leading '?' (empty if nothing remains).
    /// </summary>
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
            if (IsTrackingParam(key)) continue;
            kept.Add(pair);
        }
        return string.Join("&", kept);
    }

    private static bool IsTrackingParam(string key) =>
        key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || TrackingParams.Contains(key);

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
