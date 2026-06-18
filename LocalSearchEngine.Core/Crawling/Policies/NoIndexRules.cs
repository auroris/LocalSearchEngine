using System.Text;
using System.Text.RegularExpressions;

namespace LocalSearchEngine.Core.Crawling.Policies;

/// <summary>
/// A set of user-configured URL glob patterns marking pages that should be crawled for their links
/// but never indexed ("noindex, follow"). This is the configuration-side complement to a page
/// declaring noindex itself via a robots meta tag or <c>X-Robots-Tag</c> header: when a fetched
/// page's final URL matches any pattern here, its content is left out of the index while its
/// outlinks are still discovered and followed.
///
/// Patterns are matched against the whole normalized URL (e.g. <c>https://example.com/tag/news</c>)
/// using the same <c>*</c>/<c>$</c> glob style as robots.txt: <c>*</c> matches any run of characters
/// and a trailing <c>$</c> anchors the end of the URL; every other character is literal. Matching is
/// case-insensitive. A pattern is anchored at the start, so one without wildcards matches any URL it
/// is a prefix of unless it ends with <c>$</c> (so <c>https://example.com/about$</c> matches only
/// that exact URL). Listing a pattern here never causes a URL to be fetched — it only changes whether
/// a fetched page is indexed.
/// </summary>
public sealed class NoIndexRules
{
    private readonly List<Regex> _patterns = new();

    /// <summary>Gets a value indicating whether no patterns are configured.</summary>
    public bool IsEmpty => _patterns.Count == 0;

    /// <summary>
    /// Parses and adds a single URL glob pattern. Blank entries are rejected.
    /// </summary>
    /// <param name="pattern">The glob pattern, e.g. <c>*/tag/*</c> or <c>https://example.com/calendar/*</c>.</param>
    /// <returns><c>true</c> if the pattern was understood and added; otherwise, <c>false</c>.</returns>
    public bool Add(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        _patterns.Add(new Regex(ToRegex(pattern.Trim()), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        return true;
    }

    /// <summary>
    /// Determines whether the specified URL matches any configured noindex pattern.
    /// </summary>
    /// <param name="url">The normalized absolute URL to test.</param>
    /// <returns><c>true</c> if the URL should be followed but not indexed; otherwise, <c>false</c>.</returns>
    public bool Matches(string url)
    {
        if (_patterns.Count == 0 || string.IsNullOrEmpty(url)) return false;
        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(url)) return true;
        }
        return false;
    }

    /// <summary>
    /// Converts a URL glob pattern (<c>*</c> wildcard, optional trailing <c>$</c> end-anchor) into a
    /// regex anchored at the start, so a pattern matches from the beginning of the URL.
    /// </summary>
    /// <param name="pattern">The glob pattern.</param>
    /// <returns>A regex pattern string.</returns>
    private static string ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*')
                sb.Append(".*");
            else if (c == '$' && i == pattern.Length - 1)
                sb.Append('$');
            else
                sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }
}
