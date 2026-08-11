using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// An RSS/Atom feed fetch — the entry point for explicit update crawls, and the target of full-crawl
/// feed auto-discovery. Every listed item is offered to the frontier; the cheapness guarantee comes
/// from the items themselves, not from trusting feed dates: each enqueued item is fetched with a
/// conditional request (an unchanged article answers 304 for pennies) and only a changed content
/// hash re-embeds. Item links go through <see cref="ICrawlContext.Enqueue"/> — feed items are seed
/// material, exempt from the FollowLinks gate that keeps an update run from becoming a crawl.
/// (Deciding whether a feed proves the change list complete is the incremental planner's job,
/// before a run composes; by the time a FeedDocument is in the frontier the answer is "fetch what
/// it lists".)
/// </summary>
internal sealed class FeedDocument : Document
{
    public FeedDocument(Uri fetchUri) : base(fetchUri) { }

    public override bool IsPage => false;

    public override Document WithLocation(Uri fetchUri) => new FeedDocument(fetchUri);

    public override Task ProcessAsync(FetchResult fetch, ICrawlContext ctx, CancellationToken ct)
    {
        if (!FeedParser.TryParse(fetch.Body, FetchUri, out var items))
        {
            ctx.Logger.LogWarning("Failed to parse feed {Url}", DedupKey);
            return Task.CompletedTask;
        }

        int added = 0;
        foreach (var item in items)
        {
            if (ctx.Enqueue(new PageDocument(item.Location)))
            {
                added++;
            }
        }

        ctx.Logger.LogInformation("Feed {Url}: enqueued {Count} item(s).", DedupKey, added);
        return Task.CompletedTask;
    }
}
