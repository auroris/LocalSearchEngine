using System;
using System.Threading;
using System.Threading.Tasks;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Extraction;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// A Word document: like a PDF, it carries no in-scope outlinks, so a noindex rule simply drops it
/// from the index and the empty link sets clear out any stale links it used to have.
/// </summary>
internal sealed class DocxDocument : Document
{
    public DocxDocument(PageDocument source) : base(source.FetchUri) { }

    public override Task ProcessAsync(FetchResult fetch, ICrawlContext ctx, CancellationToken ct)
    {
        if (ctx.NoIndexRules.Matches(DedupKey))
        {
            ctx.Observer.OnPageNoIndex(DedupKey);
            ctx.Submit(new NoIndexJob(DedupKey, fetch.StatusCode, null, fetch.ETag, fetch.LastModified,
                null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<LinkEvidence>(), DocKind.Docx));
            return Task.CompletedTask;
        }

        var (title, text) = ContentExtractor.ExtractDocx(fetch.Body);

        // The title doubles as the headings text, same as the PDF path.
        return EmitIndexableAsync(fetch, ctx, title, title ?? string.Empty, text,
            Array.Empty<string>(), Array.Empty<string>(), DocKind.Docx);
    }
}
