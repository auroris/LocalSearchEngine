using System;
using System.Threading;
using System.Threading.Tasks;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Extraction;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

// NOTE: this file must not import HtmlAgilityPack — its HtmlDocument would collide with this one.
// Only ContentExtractor talks to the parser, and it never imports Pipeline.

/// <summary>
/// An HTML page: the one document kind that grows the frontier. Processing follows the old
/// producer's order exactly, because the order is load-bearing — the canonical-alias shortcut skips
/// the page's own links, links are discovered even when the page turns out unchanged or noindex
/// (otherwise an unchanged hub page orphans everything beneath it), and the noindex decision runs
/// before the unchanged/duplicate shortcuts so a page that newly declares noindex still drops its
/// previously indexed chunks.
/// </summary>
internal sealed class HtmlDocument : Document
{
    public HtmlDocument(PageDocument source) : base(source.FetchUri) { }

    public override Task ProcessAsync(FetchResult fetch, ICrawlContext ctx, CancellationToken ct)
    {
        // A user-configured noindex rule forces "follow, don't index": the page is still parsed and
        // its links followed, but its content is never indexed — exactly as if it declared noindex.
        bool userNoIndex = ctx.NoIndexRules.Matches(DedupKey);

        var analysis = ContentExtractor.AnalyzeHtml(fetch.Body, fetch.CharSet, fetch.XRobotsTag,
            DedupKey, ctx.Scope, ctx.RobotsRules, CrawlerService.UserAgent);

        // A noindex rule means "follow, don't index", so honoring a canonical alias here would be
        // wrong: aliasing skips this page's own links. Fall through to follow them instead.
        if (!userNoIndex && analysis.CanonicalAlias != null)
        {
            ctx.Observer.OnPageAlias(DedupKey, analysis.CanonicalAlias);
            if (Uri.TryCreate(analysis.CanonicalAlias, UriKind.Absolute, out var canonicalUri))
            {
                ctx.Enqueue(new PageDocument(canonicalUri));
            }
            ctx.Submit(new AliasJob(DedupKey, fetch.StatusCode));
            return Task.CompletedTask;
        }

        ctx.Observer.OnOutlinksAdded(analysis.Outlinks.Count);
        foreach (var outlink in analysis.OutlinkUris)
        {
            ctx.Discover(outlink);
        }

        if (userNoIndex || analysis.NoIndex)
        {
            ctx.Observer.OnPageNoIndex(DedupKey);
            ctx.Submit(new NoIndexJob(DedupKey, fetch.StatusCode, analysis.Title, fetch.ETag,
                fetch.LastModified, null, analysis.Outlinks, analysis.OffsiteLinks, DocKind.Html));
            return Task.CompletedTask;
        }

        return EmitIndexableAsync(fetch, ctx, analysis.Title, analysis.Headings, analysis.Text,
            analysis.Outlinks, analysis.OffsiteLinks, DocKind.Html);
    }
}
