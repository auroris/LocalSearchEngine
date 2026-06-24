using LocalSearchEngine.Core.Crawling.Reporting;
using Spectre.Console;

namespace LocalSearchEngine.Crawler;

/// <summary>
/// A fallback reporter for non-interactive output (redirected to a file or a CI log, or when
/// <c>--no-live</c> is set), where a live-updating display would just emit control-code noise.
/// Prints a line on every phase change and periodic crawl and embedding progress lines so the run is
/// still followable. The page/phase lines come from the crawler thread and the embedding lines from the
/// embedder thread, so a lock keeps the two from interleaving mid-line.
/// </summary>
internal sealed class PlainCrawlReporter : ICrawlReporter
{
    /// <summary>
    /// The number of items to process before emitting a progress line.
    /// Reduces log noise while still providing regular updates.
    /// </summary>
    private const int PageInterval = 25;

    private readonly IAnsiConsole _console;
    private readonly object _gate = new();

    /// <summary>Initializes the reporter to write to the given console.</summary>
    /// <param name="console">The console to write progress lines to.</param>
    public PlainCrawlReporter(IAnsiConsole console) => _console = console;

    /// <inheritdoc/>
    public void PhaseChanged(CrawlPhase phase, CrawlStatsSnapshot stats)
    {
        lock (_gate)
        {
            _console.MarkupLineInterpolated($"[grey]» {phase}[/]  indexed={stats.Indexed} discovered={stats.Discovered}");
        }
    }

    /// <inheritdoc/>
    public void PageProcessed(string url, CrawlOutcome outcome, CrawlStatsSnapshot stats)
    {
        // One line every N pages, so a long crawl leaves a readable trail without flooding output.
        if (stats.Processed % PageInterval != 0) return;
        lock (_gate)
        {
            _console.MarkupLineInterpolated(
                $"[grey]{stats.Processed}/{stats.Discovered}[/]  indexed={stats.Indexed} links={stats.LinksFound} removed={stats.Removed}");
        }
    }

    /// <inheritdoc/>
    public void EmbedProgress(int processed, int queued)
    {
        // A line every N items, plus the last one, so the backlog draining after the crawl finishes is
        // still followable without flooding the log.
        if (processed % PageInterval != 0 && processed != queued) return;
        lock (_gate)
        {
            _console.MarkupLineInterpolated($"[grey]embedding {processed}/{queued}[/]");
        }
    }
}
