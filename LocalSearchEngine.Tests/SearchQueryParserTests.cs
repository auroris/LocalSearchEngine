using LocalSearchEngine.Core.Searching;
using Xunit;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Covers site: token extraction (and what's left of the query afterwards) plus the
/// host-matching semantics used to filter search candidates.
/// </summary>
public class SearchQueryParserTests
{
    [Fact]
    public void Plain_query_has_no_filters_and_unchanged_text()
    {
        var parsed = SearchQueryParser.Parse("kestrel reverse proxy setup");

        Assert.Empty(parsed.SiteFilters);
        Assert.Equal("kestrel reverse proxy setup", parsed.Text);
    }

    [Fact]
    public void Site_token_is_extracted_and_removed_from_text()
    {
        var parsed = SearchQueryParser.Parse("deploy guide site:example.com");

        Assert.Equal("deploy guide", parsed.Text);
        Assert.Equal(new[] { "example.com" }, parsed.SiteFilters);
    }

    [Theory]
    [InlineData("site:https://example.com/docs", "example.com")]   // pasted URL form
    [InlineData("site:Example.COM.", "example.com")]               // case and trailing dot
    [InlineData("site:example.com:8080", "example.com")]           // port is ignored
    public void Site_values_are_normalized_to_a_bare_host(string token, string expected)
    {
        var parsed = SearchQueryParser.Parse($"{token} query words");

        Assert.Equal(new[] { expected }, parsed.SiteFilters);
        Assert.Equal("query words", parsed.Text);
    }

    [Fact]
    public void Multiple_site_tokens_are_all_collected()
    {
        var parsed = SearchQueryParser.Parse("site:a.local notes site:b.local");

        Assert.Equal("notes", parsed.Text);
        Assert.Equal(new[] { "a.local", "b.local" }, parsed.SiteFilters);
    }

    [Fact]
    public void Site_only_query_leaves_no_text()
    {
        var parsed = SearchQueryParser.Parse("site:example.com");

        Assert.Equal(string.Empty, parsed.Text);
        Assert.Single(parsed.SiteFilters);
    }

    [Fact]
    public void Site_in_the_middle_of_a_word_is_left_alone()
    {
        // "website:example.com" is not a site: token; it stays in the text untouched.
        var parsed = SearchQueryParser.Parse("website:example.com");

        Assert.Empty(parsed.SiteFilters);
        Assert.Equal("website:example.com", parsed.Text);
    }

    [Fact]
    public void Matching_covers_exact_host_and_subdomains_but_not_lookalikes()
    {
        var parsed = SearchQueryParser.Parse("x site:example.com");

        Assert.True(parsed.MatchesSite("https://example.com/page"));
        Assert.True(parsed.MatchesSite("https://docs.example.com/page"));   // subdomain
        Assert.True(parsed.MatchesSite("http://example.com:8080/page"));    // any port
        Assert.False(parsed.MatchesSite("https://badexample.com/page"));    // suffix lookalike
        Assert.False(parsed.MatchesSite("https://example.org/page"));
        Assert.False(parsed.MatchesSite("not a url"));
    }

    [Fact]
    public void No_filters_matches_everything()
    {
        var parsed = SearchQueryParser.Parse("anything");

        Assert.True(parsed.MatchesSite("https://whatever.example/page"));
    }
}
