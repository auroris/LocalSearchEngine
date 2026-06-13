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
}
