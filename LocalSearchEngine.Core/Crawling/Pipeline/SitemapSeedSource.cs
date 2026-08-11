using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// Seeds the frontier with the seed origin's sitemaps: any declared by its robots.txt plus the
/// <c>/sitemap.xml</c> convention. Only discovery happens here — each sitemap is enqueued as a
/// <see cref="SitemapDocument"/> and fetched/parsed/recursed by the workers, under the same
/// politeness and size rules as everything else. All sitemaps of the tree share one
/// <see cref="SitemapBudget"/>.
/// </summary>
internal sealed class SitemapSeedSource : ISeedSource
{
    private readonly Uri _seedUri;
    private readonly RobotsDirectory _robots;

    public SitemapSeedSource(Uri seedUri, RobotsDirectory robots)
    {
        _seedUri = seedUri;
        _robots = robots;
    }

    public async Task SeedAsync(ICrawlContext ctx, CancellationToken ct)
    {
        var seedRobots = await _robots.GetOrFetchAsync(_seedUri);
        var originKey = UrlOrigin.Key(_seedUri);
        var budget = new SitemapBudget();

        foreach (var declared in seedRobots.Sitemaps)
        {
            EnqueueSitemap(ctx, declared, originKey, seedRobots, budget);
        }
        EnqueueSitemap(ctx, new Uri(UrlOrigin.BaseUri(_seedUri), "/sitemap.xml").ToString(), originKey, seedRobots, budget);
    }

    private static void EnqueueSitemap(ICrawlContext ctx, string sitemapUrl, string originKey, RobotsRules seedRobots, SitemapBudget budget)
    {
        if (!Uri.TryCreate(sitemapUrl, UriKind.Absolute, out var sitemapUri))
        {
            return;
        }
        if (!budget.TryTake())
        {
            ctx.Logger.LogDebug("Sitemap fetch budget exhausted; skipping sitemap {Url}", sitemapUrl);
            return;
        }
        if (!ctx.Enqueue(new SitemapDocument(sitemapUri, originKey, seedRobots, budget)))
        {
            budget.Return();
            ctx.Logger.LogInformation("Skipping out-of-scope or duplicate sitemap: {Url}", sitemapUrl);
        }
    }
}
