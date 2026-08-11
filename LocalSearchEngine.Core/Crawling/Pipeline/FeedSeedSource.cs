using System;
using System.Threading;
using System.Threading.Tasks;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// Seeds an update crawl with a single <see cref="FeedDocument"/>. Composed with
/// <c>FollowLinks = false</c> and pruning off, this is the whole entry surface of a feed-driven run:
/// the feed names what changed; nothing else on the site is fetched, re-indexed, or deleted.
/// </summary>
internal sealed class FeedSeedSource : ISeedSource
{
    private readonly Uri _feedUri;

    public FeedSeedSource(Uri feedUri) => _feedUri = feedUri;

    public Task SeedAsync(ICrawlContext ctx, CancellationToken ct)
    {
        ctx.Enqueue(new FeedDocument(_feedUri));
        return Task.CompletedTask;
    }
}
