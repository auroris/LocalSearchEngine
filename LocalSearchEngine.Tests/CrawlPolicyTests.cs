using LocalSearchEngine.Core;
using Xunit;

namespace LocalSearchEngine.Tests;

public class CrawlPolicyTests
{
    [Theory]
    [InlineData("https://example.com/page", true)]            // extensionless route -> HTML
    [InlineData("https://example.com/doc.html", true)]
    [InlineData("https://example.com/doc.htm", true)]
    [InlineData("https://example.com/file.pdf", true)]
    [InlineData("https://example.com/file.docx", true)]
    [InlineData("https://example.com/image.png", false)]
    [InlineData("https://example.com/archive.zip", false)]
    public void IsIndexableExtension_filters_by_extension(string url, bool expected)
    {
        Assert.Equal(expected, CrawlPolicy.IsIndexableExtension(url));
    }

    [Fact]
    public void IsIndexableExtension_ignores_query_string()
    {
        // The "1.2" in the query must not be read as a ".2" extension.
        Assert.True(CrawlPolicy.IsIndexableExtension("https://example.com/page?v=1.2"));
        Assert.True(CrawlPolicy.IsIndexableExtension("https://example.com/report.pdf?download=true"));
        Assert.False(CrawlPolicy.IsIndexableExtension("https://example.com/logo.png?cache=abc"));
    }
}
