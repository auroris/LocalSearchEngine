using LocalSearchEngine.Core;
using Xunit;

namespace LocalSearchEngine.Tests;

public class RobotsRulesTests
{
    private const string Bot = "LocalSearchEngine-Bot/1.0";

    [Fact]
    public void Empty_content_allows_everything()
    {
        var rules = RobotsRules.Parse("", Bot);
        Assert.True(rules.IsAllowed("/anything"));
    }

    [Fact]
    public void Wildcard_disallow_blocks_matching_paths()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /private", Bot);
        Assert.False(rules.IsAllowed("/private"));
        Assert.False(rules.IsAllowed("/private/page"));
        Assert.True(rules.IsAllowed("/public"));
    }

    [Fact]
    public void Empty_disallow_allows_everything()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow:", Bot);
        Assert.True(rules.IsAllowed("/anything"));
    }

    [Fact]
    public void Allow_overrides_disallow_by_longest_match()
    {
        var content = "User-agent: *\nDisallow: /docs\nAllow: /docs/public";
        var rules = RobotsRules.Parse(content, Bot);
        Assert.False(rules.IsAllowed("/docs/secret"));
        Assert.True(rules.IsAllowed("/docs/public/intro"));
    }

    [Fact]
    public void Specific_agent_group_takes_precedence_over_wildcard()
    {
        var content =
            "User-agent: *\nDisallow: /\n\n" +
            "User-agent: LocalSearchEngine-Bot\nDisallow: /private\nAllow: /";
        var rules = RobotsRules.Parse(content, Bot);
        Assert.True(rules.IsAllowed("/public"));   // allowed by our specific group
        Assert.False(rules.IsAllowed("/private")); // blocked by our specific group
    }

    [Fact]
    public void Multiple_user_agents_share_one_group()
    {
        var content =
            "User-agent: GoogleBot\nUser-agent: LocalSearchEngine-Bot\nDisallow: /shared";
        var rules = RobotsRules.Parse(content, Bot);
        Assert.False(rules.IsAllowed("/shared"));
    }

    [Fact]
    public void Wildcard_star_in_pattern_matches_any_segment()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /*.pdf", Bot);
        Assert.False(rules.IsAllowed("/files/report.pdf"));
        Assert.True(rules.IsAllowed("/files/report.html"));
    }

    [Fact]
    public void Dollar_anchors_pattern_end()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /*.php$", Bot);
        Assert.False(rules.IsAllowed("/index.php"));
        Assert.True(rules.IsAllowed("/index.php?x=1"));
    }

    [Fact]
    public void Crawl_delay_is_parsed_for_the_chosen_group()
    {
        var rules = RobotsRules.Parse("User-agent: *\nCrawl-delay: 2.5\nDisallow: /x", Bot);
        Assert.Equal(2.5, rules.CrawlDelaySeconds);
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var content = "# a comment\nUser-agent: *   # inline\nDisallow: /private # trailing\n";
        var rules = RobotsRules.Parse(content, Bot);
        Assert.False(rules.IsAllowed("/private"));
        Assert.True(rules.IsAllowed("/other"));
    }

    [Fact]
    public void Unrelated_specific_group_does_not_apply_to_us()
    {
        // Rules targeting only GoogleBot must not restrict our bot.
        var content = "User-agent: GoogleBot\nDisallow: /\n";
        var rules = RobotsRules.Parse(content, Bot);
        Assert.True(rules.IsAllowed("/anything"));
    }

    [Fact]
    public void Sitemap_directives_are_collected_globally()
    {
        var content =
            "Sitemap: https://example.com/sitemap.xml\n" +
            "User-agent: *\nDisallow: /private\n" +
            "Sitemap: https://example.com/news-sitemap.xml\n";
        var rules = RobotsRules.Parse(content, Bot);

        Assert.Equal(
            new[] { "https://example.com/sitemap.xml", "https://example.com/news-sitemap.xml" },
            rules.Sitemaps);
        Assert.False(rules.IsAllowed("/private")); // grouping is unaffected by sitemap lines
    }

    [Fact]
    public void Sitemap_is_returned_even_with_no_applicable_group()
    {
        // Only a GoogleBot group (irrelevant to us) plus a global Sitemap line.
        var content = "User-agent: GoogleBot\nDisallow: /\nSitemap: https://example.com/s.xml\n";
        var rules = RobotsRules.Parse(content, Bot);

        Assert.True(rules.IsAllowed("/anything"));
        Assert.Equal(new[] { "https://example.com/s.xml" }, rules.Sitemaps);
    }
}
