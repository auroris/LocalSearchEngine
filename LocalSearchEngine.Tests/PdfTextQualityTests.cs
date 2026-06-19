using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Element;
using LocalSearchEngine.Core.Crawling.Extraction;
using Xunit;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Verifies the PDF extraction-quality heuristic: a PDF is only worth indexing when most of its glyphs
/// can be reversed to Unicode. The pure cases pin down the verdict thresholds; the end-to-end cases run
/// the real iText strategy over generated PDFs to confirm the glyph tally and text both come through.
/// </summary>
public class PdfTextQualityTests
{
    [Fact]
    public void Fully_mappable_text_is_high_quality()
    {
        var x = new ContentExtractor.PdfExtraction("T", "body", TotalGlyphs: 1000, MappableGlyphs: 1000);
        Assert.Equal(1.0, x.MappableFraction);
        Assert.False(x.IsLowQualityText);
    }

    [Fact]
    public void Mostly_unmappable_text_is_low_quality()
    {
        // The motivating case: the real "Temporary GRA Pass Request.pdf" measured 118 mappable
        // glyphs out of 1,833 — its form template's subset fonts carry no ToUnicode CMap.
        var x = new ContentExtractor.PdfExtraction(null, "garbage", TotalGlyphs: 1833, MappableGlyphs: 118);
        Assert.True(x.IsLowQualityText);
    }

    [Fact]
    public void No_text_layer_is_low_quality()
    {
        var x = new ContentExtractor.PdfExtraction(null, "", TotalGlyphs: 0, MappableGlyphs: 0);
        Assert.Equal(0.0, x.MappableFraction);
        Assert.True(x.IsLowQualityText);
    }

    [Theory]
    [InlineData(80, false)] // exactly the 80% bar still indexes
    [InlineData(79, true)]  // just under it is dropped
    public void The_verdict_turns_over_at_eighty_percent(int mappableGlyphs, bool expectedLowQuality)
    {
        var x = new ContentExtractor.PdfExtraction(null, "t", TotalGlyphs: 100, MappableGlyphs: mappableGlyphs);
        Assert.Equal(expectedLowQuality, x.IsLowQualityText);
    }

    [Fact]
    public void Extracts_text_from_a_normal_pdf_and_marks_it_high_quality()
    {
        var bytes = MakeTextPdf("The quick brown fox jumps over the lazy dog.");

        var x = ContentExtractor.ExtractPdf(bytes);

        Assert.Contains("quick brown fox", x.Text);
        Assert.True(x.TotalGlyphs > 0);
        Assert.Equal(x.TotalGlyphs, x.MappableGlyphs); // a standard font reverses to Unicode cleanly
        Assert.False(x.IsLowQualityText);
    }

    [Fact]
    public void A_pdf_with_no_text_layer_is_flagged_low_quality()
    {
        var bytes = MakeImageOnlyPdf();

        var x = ContentExtractor.ExtractPdf(bytes);

        Assert.Equal(0, x.TotalGlyphs);
        Assert.True(x.IsLowQualityText);
    }

    /// <summary>Builds a minimal one-page PDF containing the given text in the default (standard) font.</summary>
    private static byte[] MakeTextPdf(string text)
    {
        var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var pdf = new PdfDocument(writer))
        using (var doc = new Document(pdf))
        {
            doc.Add(new Paragraph(text));
        }
        return ms.ToArray(); // MemoryStream.ToArray works after the writer closes the stream
    }

    /// <summary>Builds a one-page PDF that draws only a filled rectangle — no text layer, like a scan.</summary>
    private static byte[] MakeImageOnlyPdf()
    {
        var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var pdf = new PdfDocument(writer))
        {
            new PdfCanvas(pdf.AddNewPage()).SetFillColor(ColorConstants.LIGHT_GRAY).Rectangle(50, 50, 200, 100).Fill();
        }
        return ms.ToArray();
    }
}
