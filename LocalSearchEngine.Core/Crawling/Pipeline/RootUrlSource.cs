using System;
using System.Threading;
using System.Threading.Tasks;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// Seeds the crawl with its root URL, after checking the seed origin's robots.txt — a disallowed
/// seed is reported rather than crawled. The robots fetch goes through the shared directory, so the
/// origin still costs one request no matter how many sources or workers ask.
/// </summary>
internal sealed class RootUrlSource : ISeedSource
{
    private readonly Uri _seedUri;
    private readonly RobotsDirectory _robots;

    public RootUrlSource(Uri seedUri, RobotsDirectory robots)
    {
        _seedUri = seedUri;
        _robots = robots;
    }

    public async Task SeedAsync(ICrawlContext ctx, CancellationToken ct)
    {
        var robots = await _robots.GetOrFetchAsync(_seedUri);
        var normalizedSeed = UrlNormalizer.Normalize(_seedUri);
        if (CrawlPolicy.IsAllowedByRobots(normalizedSeed, robots))
        {
            ctx.Enqueue(new PageDocument(_seedUri));
        }
        else
        {
            ctx.Observer.OnSeedDisallowed(normalizedSeed);
        }
    }
}
