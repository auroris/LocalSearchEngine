using System.Text.RegularExpressions;

namespace LocalSearchEngine.Core.Searching;

/// <summary>
/// Represents a parsed search query: the text to match and any <c>site:</c> host filters.
/// </summary>
public sealed class ParsedQuery
{
    /// <summary>Gets the query text with all <c>site:</c> tokens removed.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets the normalized hosts extracted from <c>site:</c> tokens; empty means no filtering.</summary>
    public IReadOnlyList<string> SiteFilters { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Determines whether a result URL passes the site filters. A filter matches its exact
    /// host and any subdomain of it ("example.com" also matches "www.example.com"), on any
    /// port; multiple filters are OR'd. With no filters every URL passes.
    /// </summary>
    /// <param name="url">The result URL to test.</param>
    /// <returns><c>true</c> if the URL's host satisfies the filters; otherwise, <c>false</c>.</returns>
    public bool MatchesSite(string url)
    {
        if (SiteFilters.Count == 0) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        foreach (var site in SiteFilters)
        {
            if (uri.Host.Equals(site, StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.Host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>
/// Splits a raw query string into match text and <c>site:</c> host filters.
/// </summary>
public static class SearchQueryParser
{
    // A site: token is recognized at a word boundary with the host attached ("site:example.com");
    // a dangling "site:" with nothing after it is left in the text as literal words.
    private static readonly Regex SiteToken = new(
        @"(?<=^|\s)site:(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses the raw query, extracting <c>site:</c> tokens and collapsing the remaining text.
    /// </summary>
    /// <param name="query">The raw query string as typed by the user.</param>
    /// <returns>A <see cref="ParsedQuery"/> with the residual text and any site filters.</returns>
    public static ParsedQuery Parse(string query)
    {
        var sites = new List<string>();
        var text = SiteToken.Replace(query ?? string.Empty, match =>
        {
            var host = NormalizeHost(match.Groups[1].Value);
            if (host.Length > 0) sites.Add(host);
            return " ";
        });
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return new ParsedQuery { Text = text, SiteFilters = sites };
    }

    /// <summary>
    /// Reduces a site filter value to a bare lowercase hostname: a pasted scheme, path,
    /// or port is tolerated and stripped ("https://Example.com:8080/docs" → "example.com").
    /// </summary>
    /// <param name="value">The raw value following <c>site:</c>.</param>
    /// <returns>The normalized hostname, or an empty string if nothing remains.</returns>
    private static string NormalizeHost(string value)
    {
        var host = value.Trim();
        int schemeSep = host.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep >= 0) host = host[(schemeSep + 3)..];
        int slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];
        int colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];
        return host.Trim('.').ToLowerInvariant();
    }
}
