using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Policies;
using Xunit;

namespace LocalSearchEngine.Tests;

public class AllowedHostsTests
{
    private static Uri U(string url) => new(url);

    [Fact]
    public void Bare_host_matches_any_scheme_and_port()
    {
        var hosts = new AllowedHosts();
        Assert.True(hosts.Add("example.com"));

        Assert.True(hosts.IsAllowed(U("http://example.com/")));
        Assert.True(hosts.IsAllowed(U("https://example.com/page")));
        Assert.True(hosts.IsAllowed(U("http://example.com:8080/page")));
        Assert.False(hosts.IsAllowed(U("http://www.example.com/"))); // www is its own host
        Assert.False(hosts.IsAllowed(U("http://other.com/")));
    }

    [Fact]
    public void Scheme_restricts_when_specified()
    {
        var hosts = new AllowedHosts();
        Assert.True(hosts.Add("https://example.com"));

        Assert.True(hosts.IsAllowed(U("https://example.com/")));
        Assert.True(hosts.IsAllowed(U("https://example.com:8443/"))); // port still unrestricted
        Assert.False(hosts.IsAllowed(U("http://example.com/")));
    }

    [Fact]
    public void Port_restricts_when_specified()
    {
        var hosts = new AllowedHosts();
        Assert.True(hosts.Add("example.com:8080"));

        Assert.True(hosts.IsAllowed(U("http://example.com:8080/")));
        Assert.True(hosts.IsAllowed(U("https://example.com:8080/"))); // scheme still unrestricted
        Assert.False(hosts.IsAllowed(U("http://example.com/")));      // default port 80
    }

    [Fact]
    public void Full_entry_restricts_scheme_and_port()
    {
        var hosts = new AllowedHosts();
        Assert.True(hosts.Add("https://example.com:8443/")); // trailing slash tolerated

        Assert.True(hosts.IsAllowed(U("https://example.com:8443/x")));
        Assert.False(hosts.IsAllowed(U("https://example.com/x")));     // default port 443
        Assert.False(hosts.IsAllowed(U("http://example.com:8443/x")));
    }

    [Fact]
    public void Seed_origin_pins_the_schemes_default_port()
    {
        var hosts = new AllowedHosts();
        hosts.AddOrigin(U("http://example.com/start")); // no explicit port -> http's port 80

        Assert.True(hosts.IsAllowed(U("http://example.com/other")));
        Assert.True(hosts.IsAllowed(U("http://example.com:80/other"))); // same origin, spelled out
        Assert.False(hosts.IsAllowed(U("http://example.com:8080/other")));
        Assert.False(hosts.IsAllowed(U("https://example.com/other")));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var hosts = new AllowedHosts();
        Assert.True(hosts.Add("HTTPS://Example.COM"));
        Assert.True(hosts.IsAllowed(U("https://EXAMPLE.com/")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/path")] // a path is not part of a host rule
    [InlineData("example.com:notaport")]
    [InlineData("example.com:0")]
    [InlineData("example.com:70000")]
    [InlineData("://example.com")]
    [InlineData("[::1")] // unterminated IPv6 bracket
    public void Invalid_entries_are_rejected(string entry)
    {
        Assert.False(new AllowedHosts().Add(entry));
    }
}
