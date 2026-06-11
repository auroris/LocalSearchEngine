using LocalSearchEngine.Core;
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
}
