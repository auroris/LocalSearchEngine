using System.Threading;
using System.Threading.Tasks;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// A module that seeds the crawl frontier — a root URL, a sitemap sweep, an RSS feed. Sources run
/// after the workers start, under the pipeline's root pending token, so the crawl cannot be declared
/// complete mid-seed no matter how the enqueues interleave with worker progress.
/// </summary>
internal interface ISeedSource
{
    /// <summary>
    /// Enqueues this source's root documents through <see cref="ICrawlContext.Enqueue"/>.
    /// </summary>
    /// <param name="ctx">The crawl context to enqueue into.</param>
    /// <param name="ct">Cancels seeding.</param>
    /// <returns>A task that completes when everything this source contributes directly is enqueued.</returns>
    Task SeedAsync(ICrawlContext ctx, CancellationToken ct);
}
