using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// Seeds the frontier with an explicit list of pages — the composition an incremental run uses once
/// the planner has already decided exactly which items changed. Composed with FollowLinks off, the
/// listed pages are the entire run.
/// </summary>
internal sealed class ListSeedSource : ISeedSource
{
    private readonly IReadOnlyList<Uri> _pages;

    public ListSeedSource(IReadOnlyList<Uri> pages) => _pages = pages;

    public Task SeedAsync(ICrawlContext ctx, CancellationToken ct)
    {
        foreach (var page in _pages)
        {
            ctx.Enqueue(new PageDocument(page));
        }
        return Task.CompletedTask;
    }
}
