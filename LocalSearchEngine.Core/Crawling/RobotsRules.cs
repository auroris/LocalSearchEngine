using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TurnerSoftware.RobotsExclusionTools;

namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Represents a parsed robots.txt ruleset, resolved using TurnerSoftware.RobotsExclusionTools.
/// Supports grouped user agents, Allow/Disallow directives with wildcards, longest-match precedence, and crawl delays.
/// </summary>
public sealed class RobotsRules
{
    private readonly List<(bool Allow, int PatternLength, Regex Matcher)> _rules = new();

    /// <summary>
    /// Gets the minimum wait delay between requests in seconds, if specified.
    /// </summary>
    public double? CrawlDelaySeconds { get; }

    /// <summary>
    /// Gets the list of absolute sitemap URLs declared in robots.txt.
    /// </summary>
    public IReadOnlyList<string> Sitemaps { get; }

    private RobotsRules(RobotsFile robotsFile, string userAgent, string content)
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

        // Fallback for float crawl delay parsing since RobotsExclusionTools parses Crawl-delay as an integer.
        if (CrawlDelaySeconds == null && !string.IsNullOrEmpty(content))
        {
            CrawlDelaySeconds = ParseCrawlDelayFallback(content, userAgent);
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
        var robotsFile = parser.FromString(content, new Uri("http://localhost"));
        return new RobotsRules(robotsFile, userAgent, content);
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

    private static double? ParseCrawlDelayFallback(string content, string userAgent)
    {
        var groups = new List<(List<string> Agents, double? CrawlDelay)>();
        var currentAgents = new List<string>();
        double? currentDelay = null;
        bool lastLineWasUserAgent = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            int colon = line.IndexOf(':');
            if (colon < 0) continue;

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (field == "user-agent")
            {
                if (!lastLineWasUserAgent)
                {
                    if (currentAgents.Count > 0 && currentDelay.HasValue)
                    {
                        groups.Add((new List<string>(currentAgents), currentDelay));
                    }
                    currentAgents.Clear();
                    currentDelay = null;
                }
                currentAgents.Add(value);
                lastLineWasUserAgent = true;
            }
            else if (field == "crawl-delay")
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var delay))
                {
                    currentDelay = delay;
                }
                lastLineWasUserAgent = false;
            }
            else
            {
                lastLineWasUserAgent = false;
            }
        }
        if (currentAgents.Count > 0 && currentDelay.HasValue)
        {
            groups.Add((currentAgents, currentDelay));
        }

        // Match user agent
        double? specificDelay = null;
        double? wildcardDelay = null;
        foreach (var group in groups)
        {
            foreach (var agent in group.Agents)
            {
                if (agent == "*")
                {
                    wildcardDelay = group.CrawlDelay;
                }
                else if (userAgent.StartsWith(agent, StringComparison.OrdinalIgnoreCase))
                {
                    specificDelay = group.CrawlDelay;
                }
            }
        }

        return specificDelay ?? wildcardDelay;
    }
}
