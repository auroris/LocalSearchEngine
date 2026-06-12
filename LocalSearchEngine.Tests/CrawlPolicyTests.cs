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

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("application/xhtml+xml", true)]
    [InlineData("application/pdf", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData("text/plain", true)]
    [InlineData("application/zip", true)]
    [InlineData("application/x-zip-compressed", true)]
    [InlineData("image/png", false)]
    [InlineData("application/json", false)]
    [InlineData("text/css", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("text/html; charset=utf-8", true)]
    public void IsSupportedOrGenericContentType_validates_against_whitelist(string? mediaType, bool expected)
    {
        Assert.Equal(expected, CrawlPolicy.IsSupportedOrGenericContentType(mediaType));
    }

    [Fact]
    public void IsSupportedPrefix_detects_magic_bytes_or_html_markup()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7");
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 };
        var html = Encoding.UTF8.GetBytes("  \n<!DOCTYPE html>");
        var junk = Encoding.UTF8.GetBytes("random junk bytes here");

        // Generic content types require magic byte checks
        Assert.True(CrawlPolicy.IsSupportedPrefix(pdf, "application/octet-stream"));
        Assert.True(CrawlPolicy.IsSupportedPrefix(zip, "application/octet-stream"));
        Assert.True(CrawlPolicy.IsSupportedPrefix(html, "application/octet-stream"));
        Assert.False(CrawlPolicy.IsSupportedPrefix(junk, "application/octet-stream"));

        // Authoritative content types do not check prefix magic bytes
        Assert.True(CrawlPolicy.IsSupportedPrefix(junk, "application/pdf"));
        Assert.True(CrawlPolicy.IsSupportedPrefix(junk, "text/html"));
    }

    [Theory]
    [InlineData("http://test.local/doc.docx", true)]
    [InlineData("http://test.local/archive.zip", false)]
    [InlineData("http://test.local/sheet.xlsx", false)]
    [InlineData("http://test.local/download", true)]
    public void IsSupportedPrefix_filters_zip_files_by_extension(string url, bool expected)
    {
        var zipMagic = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 };
        Assert.Equal(expected, CrawlPolicy.IsSupportedPrefix(zipMagic, "application/octet-stream", url));
    }
}
