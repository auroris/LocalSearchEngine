using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalSearchEngine.Core;

/// <summary>
/// A parsed robots.txt, reduced to the group that applies to a single user agent.
/// Supports grouped <c>User-agent</c> lines, <c>Allow</c>/<c>Disallow</c> with
/// <c>*</c> and <c>$</c> wildcards, longest-match precedence, and <c>Crawl-delay</c>.
/// </summary>
public sealed class RobotsRules
{
    private readonly record struct Rule(bool Allow, int PatternLength, Regex Matcher);

    private readonly List<Rule> _rules;

    public double? CrawlDelaySeconds { get; }

    private RobotsRules(List<Rule> rules, double? crawlDelaySeconds)
    {
        _rules = rules;
        CrawlDelaySeconds = crawlDelaySeconds;
    }

    /// <summary>An allow-everything ruleset (no robots.txt, or none applicable).</summary>
    public static RobotsRules AllowAll { get; } = new(new List<Rule>(), null);

    /// <summary>
    /// Whether the path (plus query) may be fetched. Among all matching rules the
    /// longest pattern wins; ties go to Allow, per the de-facto Google spec.
    /// </summary>
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
    /// Parses robots.txt content and returns the rules for <paramref name="userAgent"/>.
    /// A user-agent-specific group takes precedence over the <c>*</c> group.
    /// </summary>
    public static RobotsRules Parse(string content, string userAgent)
    {
        var groups = new List<Group>();
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
                    // A user-agent line after a rule line starts a fresh group;
                    // consecutive user-agent lines share one group.
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
            }
        }

        // Prefer a group that names this agent; otherwise fall back to "*".
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
        if (chosen is null) return AllowAll;

        var compiled = new List<Rule>();
        foreach (var (allow, path) in chosen.Directives)
        {
            // An empty Disallow means "allow everything" and an empty Allow is a
            // no-op; both contribute no constraining rule.
            if (string.IsNullOrEmpty(path)) continue;
            compiled.Add(new Rule(allow, path.Length, new Regex(ToRegex(path), RegexOptions.CultureInvariant)));
        }

        return new RobotsRules(compiled, chosen.CrawlDelay);
    }

    private static Group NewGroup(List<Group> groups)
    {
        var group = new Group();
        groups.Add(group);
        return group;
    }

    /// <summary>Converts a robots path pattern (with * and trailing $) to an anchored regex.</summary>
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

    private sealed class Group
    {
        public List<string> Agents { get; } = new();
        public List<(bool Allow, string Path)> Directives { get; } = new();
        public double? CrawlDelay { get; set; }
    }
}
