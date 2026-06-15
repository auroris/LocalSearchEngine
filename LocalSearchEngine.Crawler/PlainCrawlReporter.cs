using LocalSearchEngine.Core.Crawling.Reporting;
using Spectre.Console;

namespace LocalSearchEngine.Crawler;

/// <summary>
/// A fallback reporter for non-interactive output (redirected to a file or a CI log, or when
/// <c>--no-live</c> is set), where a live-updating display would just emit control-code noise.
/// Prints a line on every phase change and a periodic progress line so the run is still followable.
/// </summary>
internal sealed class PlainCrawlReporter : ICrawlReporter
{
    private const int PageInterval = 25;

    private readonly IAnsiConsole _console;

    /// <summary>Initializes the reporter to write to the given console.</summary>
    /// <param name="console">The console to write progress lines to.</param>
    public PlainCrawlReporter(IAnsiConsole console) => _console = console;

    /// <inheritdoc/>
    public void PhaseChanged(CrawlPhase phase, CrawlStatsSnapshot stats) =>
        _console.MarkupLineInterpolated($"[grey]» {phase}[/]  indexed={stats.Indexed} discovered={stats.Discovered}");

    /// <inheritdoc/>
    public void PageProcessed(string url, CrawlOutcome outcome, CrawlStatsSnapshot stats)
    {
        // One line every N pages, so a long crawl leaves a readable trail without flooding output.
        if (stats.Processed % PageInterval != 0) return;
        _console.MarkupLineInterpolated(
            $"[grey]{stats.Indexed}/{stats.Discovered}[/]  processed={stats.Processed} links={stats.LinksFound} removed={stats.Removed}");
    }
}
