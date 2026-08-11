using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// A sitemap fetch flowing through the same worker pipeline as pages, which is how it gains the
/// politeness gate, host-health checks, and size caps the old dedicated service lacked. Parsing is
/// hardened against hostile XML (no DTDs, no external resolution). A sitemap-index recurses by
/// enqueueing child <see cref="SitemapDocument"/>s that share one <see cref="SitemapBudget"/>; leaf
/// entries are filtered to the seed's origin and its robots rules, then enqueued as unknown pages.
/// Entries go through <see cref="ICrawlContext.Enqueue"/>, not Discover: sitemap contents are seed
/// material, so a follow-nothing plan can still crawl exactly what a sitemap lists.
/// </summary>
internal sealed class SitemapDocument : Document
{
    private readonly string _originKey;
    private readonly RobotsRules _seedRobots;
    private readonly SitemapBudget _budget;

    public SitemapDocument(Uri fetchUri, string originKey, RobotsRules seedRobots, SitemapBudget budget)
        : base(fetchUri)
    {
        _originKey = originKey;
        _seedRobots = seedRobots;
        _budget = budget;
    }

    public override bool IsPage => false;

    public override Document WithLocation(Uri fetchUri) =>
        new SitemapDocument(fetchUri, _originKey, _seedRobots, _budget);

    public override Task ProcessAsync(FetchResult fetch, ICrawlContext ctx, CancellationToken ct)
    {
        XmlDocument doc;
        try
        {
            var readerSettings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            doc = new XmlDocument { XmlResolver = null };
            using var byteStream = new MemoryStream(fetch.Body);
            using var xmlReader = XmlReader.Create(byteStream, readerSettings);
            doc.Load(xmlReader);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Failed to parse sitemap {Url}", DedupKey);
            return Task.CompletedTask;
        }

        bool isIndex = string.Equals(doc.DocumentElement?.LocalName, "sitemapindex", StringComparison.OrdinalIgnoreCase);
        int added = 0;
        foreach (XmlNode node in doc.GetElementsByTagName("loc"))
        {
            var value = node.InnerText?.Trim();
            if (string.IsNullOrEmpty(value)) continue;

            if (isIndex)
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var nestedUri)) continue;
                if (!_budget.TryTake())
                {
                    ctx.Logger.LogDebug("Sitemap fetch budget exhausted; skipping nested sitemap {Url}", value);
                    continue;
                }
                if (!ctx.Enqueue(WithLocation(nestedUri)))
                {
                    _budget.Return(); // duplicate or out of scope: no fetch will happen
                }
                continue;
            }

            if (!UrlNormalizer.TryNormalize(value, out var normalizedUrl)) continue;
            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var locUri)) continue;
            if (!string.Equals(UrlOrigin.Key(locUri), _originKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!CrawlPolicy.IsAllowedByRobots(normalizedUrl, _seedRobots)) continue;

            if (ctx.Enqueue(new PageDocument(locUri)))
            {
                added++;
            }
        }

        if (added > 0)
        {
            ctx.Logger.LogInformation("Enqueued {Count} URLs from sitemap {Url}", added, DedupKey);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// The cap on total sitemap fetches across one origin's whole sitemap tree, shared by every
/// <see cref="SitemapDocument"/> the tree spawns. The visited set already breaks cycles; this bounds
/// legitimately enormous (or maliciously deep) index trees. Tokens are taken when a sitemap is
/// enqueued and returned if the enqueue was rejected, so the count reflects fetches that will
/// actually happen.
/// </summary>
internal sealed class SitemapBudget
{
    private int _remaining;

    public SitemapBudget(int maxFetches = 200) => _remaining = maxFetches;

    /// <summary>Takes one fetch token.</summary>
    /// <returns><c>false</c> when the budget is exhausted.</returns>
    public bool TryTake() => Interlocked.Decrement(ref _remaining) >= 0;

    /// <summary>Returns a token whose enqueue was rejected (duplicate or out-of-scope sitemap).</summary>
    public void Return() => Interlocked.Increment(ref _remaining);
}
