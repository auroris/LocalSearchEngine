using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Represents a parsed robots.txt ruleset, resolved to the group that applies to a single user agent.
/// Supports grouped user agents, Allow/Disallow directives with wildcards, longest-match precedence, and crawl delays.
/// </summary>
public sealed class RobotsRules
{
    /// <summary>
    /// Represents a single parsed robots.txt rule.
    /// </summary>
    /// <param name="Allow"><c>true</c> if this is an Allow rule; <c>false</c> if it is a Disallow rule.</param>
    /// <param name="PatternLength">The length of the original pattern string, used for precedence ranking.</param>
    /// <param name="Matcher">The compiled regex used to match paths against this rule.</param>
    private readonly record struct Rule(bool Allow, int PatternLength, Regex Matcher);

    private readonly List<Rule> _rules;

    /// <summary>
    /// Gets the minimum wait delay between requests in seconds, if specified.
    /// </summary>
    public double? CrawlDelaySeconds { get; }

    /// <summary>
    /// Gets the list of absolute sitemap URLs declared in robots.txt.
    /// </summary>
    public IReadOnlyList<string> Sitemaps { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RobotsRules"/> class.
    /// </summary>
    /// <param name="rules">The list of compiled matching rules.</param>
    /// <param name="crawlDelaySeconds">The crawl delay in seconds, if any.</param>
    /// <param name="sitemaps">The global sitemaps listed.</param>
    private RobotsRules(List<Rule> rules, double? crawlDelaySeconds, IReadOnlyList<string> sitemaps)
    {
        _rules = rules;
        CrawlDelaySeconds = crawlDelaySeconds;
        Sitemaps = sitemaps;
    }

    /// <summary>
    /// Gets an allow-all ruleset with no restrictions.
    /// </summary>
    public static RobotsRules AllowAll { get; } = new(new List<Rule>(), null, Array.Empty<string>());

    /// <summary>
    /// Gets a disallow-all ruleset, blocking all crawl requests.
    /// </summary>
    public static RobotsRules DisallowAll { get; } = new(
        new List<Rule> { new Rule(false, 1, new Regex("^/", RegexOptions.CultureInvariant)) },
        null, Array.Empty<string>());

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
        var groups = new List<Group>();
        var sitemaps = new List<string>();
        Group? current = null;
        bool lastLineWasRule = false;

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

            switch (field)
            {
                case "user-agent":
                    if (current is null || lastLineWasRule)
                    {
                        current = new Group();
                        groups.Add(current);
                    }
                    current.Agents.Add(value);
                    lastLineWasRule = false;
                    break;

                case "disallow":
                    (current ??= NewGroup(groups)).Directives.Add((false, value));
                    lastLineWasRule = true;
                    break;

                case "allow":
                    (current ??= NewGroup(groups)).Directives.Add((true, value));
                    lastLineWasRule = true;
                    break;

                case "crawl-delay":
                    current ??= NewGroup(groups);
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay))
                        current.CrawlDelay = delay;
                    lastLineWasRule = true;
                    break;

                case "sitemap":
                    if (value.Length > 0) sitemaps.Add(value);
                    break;
            }
        }

        Group? specific = null;
        Group? wildcard = null;
        foreach (var group in groups)
        {
            foreach (var agent in group.Agents)
            {
                if (agent == "*")
                {
                    wildcard ??= group;
                }
                else if (userAgent.StartsWith(agent, StringComparison.OrdinalIgnoreCase))
                {
                    specific ??= group;
                }
            }
        }

        var chosen = specific ?? wildcard;
        if (chosen is null)
        {
            return sitemaps.Count == 0 ? AllowAll : new RobotsRules(new List<Rule>(), null, sitemaps);
        }

        var compiled = new List<Rule>();
        foreach (var (allow, path) in chosen.Directives)
        {
            if (string.IsNullOrEmpty(path)) continue;
            compiled.Add(new Rule(allow, path.Length, new Regex(ToRegex(path), RegexOptions.CultureInvariant)));
        }

        return new RobotsRules(compiled, chosen.CrawlDelay, sitemaps);
    }

    /// <summary>
    /// Creates and registers a new parser group.
    /// </summary>
    /// <param name="groups">The active list of user agent groups.</param>
    /// <returns>A new <see cref="Group"/> instance.</returns>
    private static Group NewGroup(List<Group> groups)
    {
        var group = new Group();
        groups.Add(group);
        return group;
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

    /// <summary>
    /// Represents a parsed group of directives for specific user agents.
    /// </summary>
    private sealed class Group
    {
        /// <summary>
        /// Gets the list of user agents this group applies to.
        /// </summary>
        public List<string> Agents { get; } = new();

        /// <summary>
        /// Gets the list of path directives (Allow/Disallow) and their patterns.
        /// </summary>
        public List<(bool Allow, string Path)> Directives { get; } = new();

        /// <summary>
        /// Gets or sets the crawl delay specified for this group in seconds.
        /// </summary>
        public double? CrawlDelay { get; set; }
    }
}
