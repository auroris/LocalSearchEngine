using System.IO.Compression;
using System.Text;
using LocalSearchEngine.Core.Crawling;
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

    [Theory]
    [InlineData("application/pdf", DocKind.Pdf)]
    [InlineData("text/html", DocKind.Html)]
    [InlineData("application/xhtml+xml", DocKind.Html)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", DocKind.Docx)]
    [InlineData("TEXT/HTML", DocKind.Html)]           // case-insensitive
    [InlineData("application/json", DocKind.Unknown)] // not a type we index
    public void ClassifyContent_prefers_the_declared_content_type(string contentType, DocKind expected)
    {
        // When the Content-Type is authoritative the body is irrelevant.
        Assert.Equal(expected, CrawlPolicy.ClassifyContent(contentType, Array.Empty<byte>()));
    }

    [Fact]
    public void ClassifyContent_sniffs_when_content_type_is_missing_or_generic()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7 (rest of a pdf body)");
        var html = Encoding.UTF8.GetBytes("  \n<!DOCTYPE html><html><body>hi</body></html>");
        var json = Encoding.UTF8.GetBytes("{\"k\":1}");

        Assert.Equal(DocKind.Pdf, CrawlPolicy.ClassifyContent("application/octet-stream", pdf));
        Assert.Equal(DocKind.Pdf, CrawlPolicy.ClassifyContent(null, pdf));
        Assert.Equal(DocKind.Html, CrawlPolicy.ClassifyContent("application/octet-stream", html));
        Assert.Equal(DocKind.Unknown, CrawlPolicy.ClassifyContent("application/octet-stream", json));
    }

    [Fact]
    public void ClassifyContent_sniffs_docx_only_for_a_zip_with_the_word_part()
    {
        var docx = Zip("[Content_Types].xml", "word/document.xml");
        var xlsx = Zip("[Content_Types].xml", "xl/workbook.xml");

        Assert.Equal(DocKind.Docx, CrawlPolicy.ClassifyContent("application/octet-stream", docx));
        Assert.Equal(DocKind.Unknown, CrawlPolicy.ClassifyContent("application/octet-stream", xlsx));
    }

    /// <summary>Builds an in-memory zip containing the named (tiny) entries.</summary>
    private static byte[] Zip(params string[] entryNames)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entryNames)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write("<x/>"u8);
            }
        }
        return ms.ToArray();
    }
}
