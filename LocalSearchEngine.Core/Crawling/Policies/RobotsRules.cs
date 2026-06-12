using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TurnerSoftware.RobotsExclusionTools;

namespace LocalSearchEngine.Core.Crawling.Policies;

/// <summary>
/// Represents a parsed robots.txt ruleset, resolved using TurnerSoftware.RobotsExclusionTools.
/// Supports grouped user agents, Allow/Disallow directives with wildcards, longest-match precedence, and crawl delays.
/// </summary>
public sealed class RobotsRules
{
    // Declared before the static AllowAll/DisallowAll properties: their initializers run
    // Parse, which needs this regex, and static initializers execute in declaration order.
    private static readonly Regex FractionalCrawlDelay = new(
        @"^([ \t]*crawl-delay[ \t]*:[ \t]*)(\d*\.\d+)(?![\d.])",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly List<(bool Allow, int PatternLength, Regex Matcher)> _rules = new();

    /// <summary>
    /// Gets the minimum wait delay between requests in seconds, if specified. Fractional
    /// values in the file (e.g. "0.5") are rounded up to the next whole second.
    /// </summary>
    public double? CrawlDelaySeconds { get; }

    /// <summary>
    /// Gets the list of absolute sitemap URLs declared in robots.txt.
    /// </summary>
    public IReadOnlyList<string> Sitemaps { get; }

    private RobotsRules(RobotsFile robotsFile, string userAgent)
    {
        // Extract Sitemaps
        Sitemaps = robotsFile.SitemapEntries.Select(s => s.Sitemap.ToString()).ToList();

        // Resolve the entry for the user agent, falling back to wildcard
        bool hasEntry = false;
        SiteAccessEntry actualEntry = default;

        if (robotsFile.TryGetEntryForUserAgent(userAgent, out var entry))
        {
            hasEntry = true;
            actualEntry = entry;
        }
        else if (robotsFile.TryGetEntryForUserAgent("*", out var wildcardEntry))
        {
            hasEntry = true;
            actualEntry = wildcardEntry;
        }

        if (hasEntry)
        {
            CrawlDelaySeconds = actualEntry.CrawlDelay.HasValue ? (double)actualEntry.CrawlDelay.Value : null;

            foreach (var rule in actualEntry.PathRules)
            {
                var path = rule.Path;
                if (string.IsNullOrEmpty(path)) continue;

                bool isAllow = rule.RuleType.ToString().Equals("Allow", StringComparison.OrdinalIgnoreCase);
                var regex = new Regex(ToRegex(path), RegexOptions.CultureInvariant);
                _rules.Add((isAllow, path.Length, regex));
            }
        }
    }

    /// <summary>
    /// Gets an allow-all ruleset with no restrictions.
    /// </summary>
    public static RobotsRules AllowAll { get; } = Parse(string.Empty, "*");

    /// <summary>
    /// Gets a disallow-all ruleset, blocking all crawl requests.
    /// </summary>
    public static RobotsRules DisallowAll { get; } = Parse("User-agent: *\nDisallow: /", "*");

    /// <summary>
    /// Determines whether fetching is allowed for the specified path and query.
    /// </summary>
    /// <param name="pathAndQuery">The request path and query string.</param>
    /// <returns><c>true</c> if the path is allowed; otherwise, <c>false</c>.</returns>
    public bool IsAllowed(string pathAndQuery)
    {
        if (string.IsNullOrEmpty(pathAndQuery)) pathAndQuery = "/";

        bool decided = false;
        bool allowed = true;
        int bestLength = -1;

        foreach (var rule in _rules)
        {
            if (!rule.Matcher.IsMatch(pathAndQuery)) continue;
            if (rule.PatternLength > bestLength ||
                (rule.PatternLength == bestLength && rule.Allow))
            {
                bestLength = rule.PatternLength;
                allowed = rule.Allow;
                decided = true;
            }
        }

        return !decided || allowed;
    }

    /// <summary>
    /// Parses the content of a robots.txt file and returns the rules applicable to the specified user agent.
    /// </summary>
    /// <param name="content">The plain text content of the robots.txt file.</param>
    /// <param name="userAgent">The user agent string of the crawler.</param>
    /// <returns>A <see cref="RobotsRules"/> instance containing the matching rules.</returns>
    public static RobotsRules Parse(string content, string userAgent)
    {
        var parser = new RobotsFileParser();
        var robotsFile = parser.FromString(NormalizeFractionalCrawlDelays(content), new Uri("http://localhost"));
        return new RobotsRules(robotsFile, userAgent);
    }

    /// <summary>
    /// Rounds fractional Crawl-delay values (e.g. "0.5") up to whole seconds before parsing,
    /// because RobotsExclusionTools reads the directive as an integer and would otherwise drop
    /// them. Rewriting the value in the raw text keeps the library's user-agent group matching
    /// as the single authority on which group's delay applies.
    /// </summary>
    /// <param name="content">The raw robots.txt content.</param>
    /// <returns>The content with fractional crawl delays rounded up to integers.</returns>
    private static string NormalizeFractionalCrawlDelays(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        return FractionalCrawlDelay.Replace(content, m =>
            m.Groups[1].Value + Math.Ceiling(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
                .ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Converts a robots path pattern string into a regular expression.
    /// </summary>
    /// <param name="pattern">The pattern string (supporting '*' and '$').</param>
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
