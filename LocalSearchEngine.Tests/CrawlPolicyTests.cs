using System.IO.Compression;
using System.Text;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Policies;
using Xunit;

namespace LocalSearchEngine.Tests;

public class CrawlPolicyTests
{
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
