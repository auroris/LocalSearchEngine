using LocalSearchEngine.Core.Crawling.Policies;
using Xunit;

namespace LocalSearchEngine.Tests;

public class NoIndexRulesTests
{
    [Fact]
    public void Empty_ruleset_matches_nothing()
    {
        var rules = new NoIndexRules();
        Assert.True(rules.IsEmpty);
        Assert.False(rules.Matches("https://example.com/anything"));
    }

    [Fact]
    public void Wildcard_matches_anywhere_in_the_url()
    {
        var rules = new NoIndexRules();
        Assert.True(rules.Add("*/tag/*"));

        Assert.True(rules.Matches("https://example.com/tag/news"));
        Assert.True(rules.Matches("http://other.org/blog/tag/x/page/2"));
        Assert.False(rules.Matches("https://example.com/tags/news")); // needs the whole "/tag/" segment
        Assert.False(rules.Matches("https://example.com/articles"));
    }

    [Fact]
    public void Prefix_pattern_matches_a_section()
    {
        var rules = new NoIndexRules();
        Assert.True(rules.Add("https://example.com/calendar/*"));

        Assert.True(rules.Matches("https://example.com/calendar/2026/06"));
        Assert.False(rules.Matches("https://example.com/blog/2026"));
        Assert.False(rules.Matches("http://example.com/calendar/x")); // scheme differs (http vs https)
    }

    [Fact]
    public void Trailing_dollar_anchors_the_end_for_an_exact_match()
    {
        var rules = new NoIndexRules();
        Assert.True(rules.Add("https://example.com/about$"));

        Assert.True(rules.Matches("https://example.com/about"));
        Assert.False(rules.Matches("https://example.com/about-us")); // end is anchored
        Assert.False(rules.Matches("https://example.com/about/team"));
    }

    [Fact]
    public void Pattern_without_wildcard_or_anchor_is_a_prefix_match()
    {
        var rules = new NoIndexRules();
        Assert.True(rules.Add("https://example.com/about"));

        Assert.True(rules.Matches("https://example.com/about"));
        Assert.True(rules.Matches("https://example.com/about-us")); // no anchor, so it matches as a prefix
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var rules = new NoIndexRules();
        Assert.True(rules.Add("*/TAG/*"));
        Assert.True(rules.Matches("https://example.com/tag/news"));
    }

    [Fact]
    public void A_whole_host_can_be_excluded_from_indexing()
    {
        var rules = new NoIndexRules();
        Assert.True(rules.Add("*://wiki.example.com/*"));

        Assert.True(rules.Matches("https://wiki.example.com/Page"));
        Assert.True(rules.Matches("http://wiki.example.com/Other"));
        Assert.False(rules.Matches("https://example.com/wiki")); // different host
    }

    [Fact]
    public void Query_string_junk_can_be_matched()
    {
        var rules = new NoIndexRules();
        Assert.True(rules.Add("*?replytocom=*"));

        Assert.True(rules.Matches("https://example.com/post?replytocom=42"));
        Assert.False(rules.Matches("https://example.com/post"));
    }

    [Fact]
    public void Any_of_several_patterns_can_match()
    {
        var rules = new NoIndexRules();
        Assert.True(rules.Add("*/tag/*"));
        Assert.True(rules.Add("*/category/*"));

        Assert.True(rules.Matches("https://example.com/tag/news"));
        Assert.True(rules.Matches("https://example.com/category/sport"));
        Assert.False(rules.Matches("https://example.com/articles/1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_patterns_are_rejected(string? pattern)
    {
        var rules = new NoIndexRules();
        Assert.False(rules.Add(pattern));
        Assert.True(rules.IsEmpty);
    }
}
