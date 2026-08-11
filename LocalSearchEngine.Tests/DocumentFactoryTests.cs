using System.Text;
using LocalSearchEngine.Core.Crawling.Pipeline;
using Xunit;

namespace LocalSearchEngine.Tests;

public sealed class DocumentFactoryTests
{
    private static PageDocument Page() => new(new Uri("http://test.local/doc"));

    private static FetchResult Fetch(string? contentType, byte[] body) =>
        new(200, body, contentType, null, null, null, null, null);

    [Fact]
    public void Declared_html_resolves_to_html_document()
    {
        var doc = DocumentFactory.Resolve(Page(), Fetch("text/html", Encoding.UTF8.GetBytes("<p>hi</p>")));
        Assert.IsType<LocalSearchEngine.Core.Crawling.Pipeline.HtmlDocument>(doc);
    }

    [Fact]
    public void Declared_pdf_wins_over_html_looking_body()
    {
        var doc = DocumentFactory.Resolve(Page(), Fetch("application/pdf", Encoding.UTF8.GetBytes("<html>not really</html>")));
        Assert.IsType<LocalSearchEngine.Core.Crawling.Pipeline.PdfDocument>(doc);
    }

    [Fact]
    public void Generic_content_type_sniffs_pdf_magic()
    {
        var doc = DocumentFactory.Resolve(Page(), Fetch("application/octet-stream", Encoding.ASCII.GetBytes("%PDF-1.7 ...")));
        Assert.IsType<LocalSearchEngine.Core.Crawling.Pipeline.PdfDocument>(doc);
    }

    [Fact]
    public void Generic_content_type_sniffs_html()
    {
        var doc = DocumentFactory.Resolve(Page(), Fetch(null, Encoding.UTF8.GetBytes("  <!DOCTYPE html><html><body>x</body></html>")));
        Assert.IsType<LocalSearchEngine.Core.Crawling.Pipeline.HtmlDocument>(doc);
    }

    [Fact]
    public void Unclassifiable_body_resolves_to_null()
    {
        var doc = DocumentFactory.Resolve(Page(), Fetch("application/octet-stream", new byte[] { 0x00, 0x01, 0x02, 0x03 }));
        Assert.Null(doc);
    }
}
