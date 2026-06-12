using System.Globalization;

namespace LocalSearchEngine.Core.Crawling.Policies;

/// <summary>
/// The set of hosts a crawl may touch. Each rule is a hostname with an optional scheme and
/// port restriction; a component that is not specified matches anything. This is purely a
/// filter — listing a host here never causes it to be contacted. Robots.txt and pages are
/// only fetched for origins the crawl actually reaches.
/// </summary>
public sealed class AllowedHosts
{
    private readonly record struct HostRule(string? Scheme, string Host, int? Port);

    private readonly List<HostRule> _rules = new();

    /// <summary>
    /// Parses and adds a configured entry of the form <c>[scheme://]host[:port]</c>.
    /// An omitted scheme matches any scheme; an omitted port matches any port. The
    /// "www." variant of a host is NOT implied — list it as its own entry to crawl both.
    /// </summary>
    /// <param name="entry">The entry to parse, e.g. "example.com", "https://example.com", or "example.com:8080".</param>
    /// <returns><c>true</c> if the entry was understood and added; otherwise, <c>false</c>.</returns>
    public bool Add(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;
        var rest = entry.Trim();

        string? scheme = null;
        int schemeSep = rest.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep >= 0)
        {
            scheme = rest[..schemeSep].ToLowerInvariant();
            rest = rest[(schemeSep + 3)..];
            if (!Uri.CheckSchemeName(scheme)) return false;
        }

        rest = rest.TrimEnd('/'); // tolerate a pasted trailing slash
        if (rest.Length == 0 || rest.Contains('/')) return false; // a path is not part of a host rule

        int? port = null;
        string host;
        if (rest.StartsWith('['))
        {
            // Bracketed IPv6 literal, optionally followed by :port. Uri.Host keeps the
            // brackets for IPv6, so the rule stores them too and comparisons line up.
            int close = rest.IndexOf(']');
            if (close < 0) return false;
            host = rest[..(close + 1)];
            var after = rest[(close + 1)..];
            if (after.Length > 0 && (after[0] != ':' || !TryParsePort(after[1..], out port))) return false;
        }
        else
        {
            int colon = rest.IndexOf(':');
            if (colon >= 0)
            {
                if (rest.IndexOf(':', colon + 1) >= 0) return false; // unbracketed IPv6 is ambiguous
                if (!TryParsePort(rest[(colon + 1)..], out port)) return false;
                host = rest[..colon];
            }
            else
            {
                host = rest;
            }
        }

        if (host.Length == 0) return false;
        _rules.Add(new HostRule(scheme, host.ToLowerInvariant(), port));
        return true;
    }

    /// <summary>
    /// Adds the exact origin (scheme, host, and resolved port) of an absolute URI. Used for
    /// the seed URL: a seed without an explicit port allows only the scheme's default port.
    /// </summary>
    /// <param name="uri">The absolute URI whose origin to allow.</param>
    public void AddOrigin(Uri uri) => _rules.Add(new HostRule(uri.Scheme, uri.Host, uri.Port));

    /// <summary>
    /// Determines whether the specified URI falls within the allowed set.
    /// </summary>
    /// <param name="uri">The absolute URI to test.</param>
    /// <returns><c>true</c> if any rule matches the URI's scheme, host, and port; otherwise, <c>false</c>.</returns>
    public bool IsAllowed(Uri uri)
    {
        foreach (var rule in _rules)
        {
            if (rule.Scheme is not null && !string.Equals(rule.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(rule.Host, uri.Host, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Port is int port && port != uri.Port) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parses a port suffix, accepting only plain digits in the valid port range.
    /// </summary>
    /// <param name="value">The text after the ':' separator.</param>
    /// <param name="port">The parsed port number, or <c>null</c> on failure.</param>
    /// <returns><c>true</c> if the port parsed; otherwise, <c>false</c>.</returns>
    private static bool TryParsePort(string value, out int? port)
    {
        port = null;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)) return false;
        if (parsed is < 1 or > 65535) return false;
        port = parsed;
        return true;
    }
}
