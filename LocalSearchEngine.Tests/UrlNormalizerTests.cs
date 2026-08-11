using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Policies;
using Xunit;

namespace LocalSearchEngine.Tests;

public class UrlNormalizerTests
{
    [Theory]
    [InlineData("https://example.com/page#section", "https://example.com/page")]
    [InlineData("https://example.com/page/", "https://example.com/page")]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://example.com/a/b/?q=1", "https://example.com/a/b?q=1")]
    public void Normalize_canonicalizes(string input, string expected)
    {
        Assert.Equal(expected, UrlNormalizer.Normalize(new Uri(input)));
    }

    [Fact]
    public void Normalize_preserves_query_string()
    {
        var result = UrlNormalizer.Normalize(new Uri("https://example.com/search?q=hello&p=2#top"));
        Assert.Equal("https://example.com/search?q=hello&p=2", result);
    }

    [Theory]
    // utm_* and click ids are dropped; meaningful params (and their order) survive.
    [InlineData("https://example.com/p?utm_source=news&utm_medium=email", "https://example.com/p")]
    [InlineData("https://example.com/p?q=1&utm_campaign=spring&p=2", "https://example.com/p?q=1&p=2")]
    [InlineData("https://example.com/p?gclid=abc123", "https://example.com/p")]
    [InlineData("https://example.com/p?id=7&fbclid=xyz", "https://example.com/p?id=7")]
    [InlineData("https://example.com/p?ref=home", "https://example.com/p?ref=home")] // 'ref' is not treated as tracking
    public void Normalize_strips_tracking_parameters(string input, string expected)
    {
        Assert.Equal(expected, UrlNormalizer.Normalize(new Uri(input)));
    }

    [Fact]
    public void TryNormalize_rejects_relative_urls()
    {
        Assert.False(UrlNormalizer.TryNormalize("/relative/path", out _));
        Assert.False(UrlNormalizer.TryNormalize(null, out _));
    }

    [Fact]
    public void TryNormalize_accepts_absolute_urls()
    {
        Assert.True(UrlNormalizer.TryNormalize("https://example.com/x/", out var normalized));
        Assert.Equal("https://example.com/x", normalized);
    }

    // The encoding contract. Normalize's output is the URL's *stored identity* — every CrawlState
    // row, link edge, and dedup key in an existing database is in this form — so its escaping
    // behavior is frozen: valid UTF-8 escapes and spaces come out as display characters, reserved
    // and invalid escapes stay escaped. Fetching no longer round-trips through this form (the
    // pipeline fetches the exact resolved Uri), but the identity itself must never drift or every
    // existing database orphans.
    [Theory]
    [InlineData("http://h/a%20b/c.html", "http://h/a b/c.html")]     // %20 → literal space
    [InlineData("http://h/caf%C3%A9/x", "http://h/café/x")]          // valid UTF-8 escape → character
    [InlineData("http://h/a%2Fb/c", "http://h/a%2Fb/c")]             // escaped slash must not change path structure
    [InlineData("http://h/p%25s/x", "http://h/p%25s/x")]             // escaped percent stays (unescaping would re-interpret)
    [InlineData("http://h/p?q=a%26b", "http://h/p?q=a%26b")]         // escaped ampersand stays (a literal one splits the query)
    [InlineData("http://h/p?q=a%3Db", "http://h/p?q=a%3Db")]         // escaped equals stays
    public void Normalize_escaping_contract_is_stable(string input, string expected)
    {
        Assert.Equal(expected, UrlNormalizer.Normalize(new Uri(input)));
    }

    // Stored URLs get re-parsed and re-normalized (stored outlinks on a 304, prune candidates,
    // duplicate targets); if a second pass produced a different string, a URL's identity would
    // drift between crawls and rows would orphan.
    [Theory]
    [InlineData("http://h/a%20b/c.html")]
    [InlineData("http://h/caf%C3%A9/x")]
    [InlineData("http://h/a%2Fb/c")]
    [InlineData("http://h/p%25s/x")]
    [InlineData("http://h/dir/page?q=a%26b&r=2")]
    [InlineData("http://h/~user/index.html")]
    public void Normalize_is_idempotent(string input)
    {
        var once = UrlNormalizer.Normalize(new Uri(input));
        var twice = UrlNormalizer.Normalize(new Uri(once));
        Assert.Equal(once, twice);
    }
}
