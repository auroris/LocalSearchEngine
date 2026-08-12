using System;
using System.Threading;
using System.Threading.Tasks;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Extraction;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

// NOTE: this file must not import iText — its PdfDocument would collide with this one.

/// <summary>
/// A PDF: no in-scope outlinks to follow, so a noindex rule simply drops it from the index. A PDF
/// whose text extracts as font-encoding garbage (or that has no text layer) is worse than useless in
/// the index, so it is dropped like a noindex page but flagged distinctly in the run stats.
/// </summary>
internal sealed class PdfDocument : Document
{
    public PdfDocument(PageDocument source) : base(source.FetchUri) { }

    public override Task ProcessAsync(FetchResult fetch, ICrawlContext ctx, CancellationToken ct)
    {
        if (ctx.NoIndexRules.Matches(DedupKey))
        {
            ctx.Observer.OnPageNoIndex(DedupKey);
            ctx.Submit(new NoIndexJob(DedupKey, fetch.StatusCode, null, fetch.ETag, fetch.LastModified,
                null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<LinkEvidence>(), DocKind.Pdf));
            return Task.CompletedTask;
        }

        var pdf = ContentExtractor.ExtractPdf(fetch.Body);
        if (pdf.IsLowQualityText)
        {
            ctx.Observer.OnPageLowQualityText(DedupKey, pdf.MappableFraction, pdf.TotalGlyphs);
            ctx.Submit(new NoIndexJob(DedupKey, fetch.StatusCode, pdf.Title, fetch.ETag, fetch.LastModified,
                null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<LinkEvidence>(), DocKind.Pdf));
            return Task.CompletedTask;
        }

        // The title doubles as the headings text: a document has no h1–h6, and the title is the one
        // line that deserves the heading boost at search time.
        return EmitIndexableAsync(fetch, ctx, pdf.Title, pdf.Title ?? string.Empty, pdf.Text,
            Array.Empty<string>(), Array.Empty<string>(), DocKind.Pdf);
    }
}
