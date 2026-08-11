using System.Text;
using LocalSearchEngine.Core.Crawling.Extraction;
using LocalSearchEngine.Core.Crawling.Policies;
using Xunit;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Verifies ContentExtractor's encoding resolution: the HTTP header's charset rules when
/// present, a meta-declared encoding is honored when the header is silent (the page is
/// re-decoded rather than mojibake'd through a UTF-8 assumption), and bare UTF-8 still works.
/// </summary>
public class ContentExtractorTests
{
    static ContentExtractorTests()
    {
        // The tests build windows-1252 bytes themselves, so the legacy code pages must be
        // available before ContentExtractor's own static constructor would register them.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static ContentExtractor.HtmlAnalysis Analyze(byte[] body, string? httpCharset)
    {
        var hosts = new AllowedHosts();
        hosts.Add("test.local");
        return ContentExtractor.AnalyzeHtml(
            body, httpCharset, xRobotsTag: null, "http://test.local/page",
            hosts, new Dictionary<string, RobotsRules>(), "localsearchengine-bot");
    }

    [Fact]
    public void Meta_charset_is_honored_when_http_header_is_silent()
    {
        // 0xE9 ("é") is an invalid byte sequence in UTF-8; only a re-decode with the
        // meta-declared windows-1252 can produce the right characters.
        var html = "<html><head><meta charset=\"windows-1252\"><title>Café</title></head>" +
                   "<body><p>déjà vu in São Paulo</p></body></html>";
        var body = Encoding.GetEncoding(1252).GetBytes(html);

        var analysis = Analyze(body, httpCharset: null);

        Assert.Equal("Café", analysis.Title);
        Assert.Contains("déjà", analysis.Text);
    }

    [Fact]
    public void Meta_http_equiv_content_type_charset_is_honored()
    {
        var html = "<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=windows-1252\">" +
                   "<title>Entrée</title></head><body><p>crème brûlée</p></body></html>";
        var body = Encoding.GetEncoding(1252).GetBytes(html);

        var analysis = Analyze(body, httpCharset: null);

        Assert.Equal("Entrée", analysis.Title);
        Assert.Contains("brûlée", analysis.Text);
    }

    [Fact]
    public void Http_header_charset_wins_over_meta_declaration()
    {
        // The body really is windows-1252 (per the header); the meta lies about UTF-8. Decoding
        // must follow the header, so the accented characters still come out right.
        var html = "<html><head><meta charset=\"utf-8\"><title>Café</title></head>" +
                   "<body><p>déjà vu</p></body></html>";
        var body = Encoding.GetEncoding(1252).GetBytes(html);

        var analysis = Analyze(body, httpCharset: "windows-1252");

        Assert.Equal("Café", analysis.Title);
        Assert.Contains("déjà", analysis.Text);
    }

    [Fact]
    public void Utf8_body_with_no_declarations_decodes_correctly()
    {
        var html = "<html><head><title>Smörgåsbord</title></head><body><p>naïve café</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html); // no BOM, no meta, no header

        var analysis = Analyze(body, httpCharset: null);

        Assert.Equal("Smörgåsbord", analysis.Title);
        Assert.Contains("naïve", analysis.Text);
    }

    [Fact]
    public void Matching_meta_charset_does_not_break_utf8_pages()
    {
        // The common case: UTF-8 bytes that also declare UTF-8. No reload should be needed,
        // and the text must come through intact either way.
        var html = "<html><head><meta charset=\"utf-8\"><title>Über</title></head><body><p>Größe—naïve</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);

        var analysis = Analyze(body, httpCharset: null);

        Assert.Equal("Über", analysis.Title);
        Assert.Contains("Größe", analysis.Text);
    }

    [Fact]
    public void Href_entities_are_decoded_before_the_url_is_parsed()
    {
        // The only valid way to put '&' in an HTML attribute is to escape it (&amp;). HtmlAgilityPack
        // hands back that raw text, so without de-entitizing, the literal "&amp;" lands in the stored
        // URL and the query string is wrong — the real cause of legacy .asp links 404ing in the crawl.
        var html = "<html><body><a href=\"/form.asp?WSAOLANG=E&amp;WSAOTYPE=06\">x</a></body></html>";
        var body = Encoding.UTF8.GetBytes(html);

        var analysis = Analyze(body, httpCharset: null);

        Assert.Contains("http://test.local/form.asp?WSAOLANG=E&WSAOTYPE=06", analysis.Outlinks);
        Assert.DoesNotContain(analysis.Outlinks, u => u.Contains("&amp;"));
    }

    [Fact]
    public void Meta_robots_name_is_matched_case_insensitively()
    {
        var html = "<html><head><meta name=\"ROBOTS\" content=\"noindex\"><title>T</title></head><body><p>x</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);

        var analysis = Analyze(body, httpCharset: null);

        Assert.True(analysis.NoIndex);
    }

    [Fact]
    public void Bot_specific_meta_name_targets_this_crawler_regardless_of_token_casing()
    {
        // A page targeting our bot by name must be honored even when the configured user-agent token
        // is mixed-case (as the real CrawlerService.UserAgent is). Here the meta name and the token
        // differ only in case, so this fails unless the comparison is case-insensitive.
        var html = "<html><head><meta name=\"localsearchengine-bot/1.0\" content=\"noindex, nofollow\">" +
                   "<title>T</title></head><body><p>x</p> <a href=\"/other\">o</a></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var hosts = new AllowedHosts();
        hosts.Add("test.local");

        var analysis = ContentExtractor.AnalyzeHtml(
            body, null, xRobotsTag: null, "http://test.local/page",
            hosts, new Dictionary<string, RobotsRules>(), "LocalSearchEngine-Bot/1.0");

        Assert.True(analysis.NoIndex);
        Assert.True(analysis.NoFollow);
    }

    [Theory]
    [InlineData("noindex, unavailable_after: 1 Jan 2030 00:00:00 GMT", true, false)]
    [InlineData("noindex, max-image-preview: large", true, false)]
    [InlineData("googlebot: noindex, nofollow", true, true)]
    [InlineData("otherbot: noindex, nofollow", false, false)]
    public void XRobotsTag_directives_are_parsed_correctly(string headerValue, bool expectedNoIndex, bool expectedNoFollow)
    {
        var html = "<html><head><title>Test</title></head><body><p>Hello</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var hosts = new AllowedHosts();
        hosts.Add("test.local");

        var analysis = ContentExtractor.AnalyzeHtml(
            body, null, xRobotsTag: headerValue, "http://test.local/page",
            hosts, new Dictionary<string, RobotsRules>(), "googlebot");

        Assert.Equal(expectedNoIndex, analysis.NoIndex);
        Assert.Equal(expectedNoFollow, analysis.NoFollow);
    }

    [Fact]
    public void Advertised_rss_and_atom_feeds_are_extracted_and_resolved()
    {
        var html = "<html><head><title>T</title>" +
                   "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"/rss.xml\">" +
                   "<link rel=\"alternate\" type=\"application/atom+xml\" href=\"http://test.local/atom.xml\">" +
                   "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"/rss.xml\">" + // duplicate
                   "</head><body><p>x</p></body></html>";

        var analysis = Analyze(Encoding.UTF8.GetBytes(html), null);

        Assert.Equal(
            new[] { "http://test.local/rss.xml", "http://test.local/atom.xml" },
            analysis.AdvertisedFeedUris.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void Non_feed_link_elements_are_not_mistaken_for_feeds()
    {
        var html = "<html><head><title>T</title>" +
                   "<link rel=\"stylesheet\" type=\"text/css\" href=\"/site.css\">" +
                   "<link rel=\"alternate\" type=\"text/html\" hreflang=\"fr\" href=\"/fr/page\">" + // translation, not a feed
                   "<link rel=\"alternate stylesheet\" type=\"application/rss+xml\" href=\"/weird.xml\">" + // token list still counts as alternate
                   "<link rel=\"preload\" type=\"application/rss+xml\" href=\"/not-alternate.xml\">" +
                   "</head><body><p>x</p></body></html>";

        var analysis = Analyze(Encoding.UTF8.GetBytes(html), null);

        Assert.Equal(new[] { "http://test.local/weird.xml" }, analysis.AdvertisedFeedUris.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void Feeds_are_advertised_even_on_nofollow_pages()
    {
        // Feed advertisement is discovery metadata like the canonical link, not an
        // endorsement-carrying anchor, so nofollow (which suppresses link extraction) leaves it alone.
        var html = "<html><head><title>T</title><meta name=\"robots\" content=\"nofollow\">" +
                   "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"/rss.xml\">" +
                   "</head><body><p>x</p> <a href=\"/other\">o</a></body></html>";

        var analysis = Analyze(Encoding.UTF8.GetBytes(html), null);

        Assert.True(analysis.NoFollow);
        Assert.Empty(analysis.Outlinks);
        Assert.Equal(new[] { "http://test.local/rss.xml" }, analysis.AdvertisedFeedUris.Select(u => u.AbsoluteUri));
    }
}
