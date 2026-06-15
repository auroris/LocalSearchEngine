using LocalSearchEngine.Core.Crawling.Reporting;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LocalSearchEngine.Crawler;

/// <summary>
/// Renders live crawl progress to an interactive terminal: a progress bar (indexed / discovered,
/// the denominator growing as the crawl finds links), a panel of running totals, and a rolling list
/// of the most recent URLs with how each resolved. Every callback arrives on the crawler's single
/// producer thread, so no locking is needed; updates are lightly throttled to keep redraws cheap.
/// </summary>
internal sealed class SpectreCrawlReporter : ICrawlReporter
{
    private const int RecentCapacity = 10;
    private const int BarWidth = 40;
    private static readonly TimeSpan MinRedrawGap = TimeSpan.FromMilliseconds(50);

    private readonly LiveDisplayContext _live;
    private readonly Queue<(string Url, CrawlOutcome Outcome)> _recent = new();
    private CrawlStatsSnapshot _stats;
    private DateTime _lastRenderUtc = DateTime.MinValue;

    /// <summary>Initializes the reporter to drive the given live display.</summary>
    /// <param name="live">The Spectre live-display context the crawl runs inside.</param>
    public SpectreCrawlReporter(LiveDisplayContext live) => _live = live;

    /// <inheritdoc/>
    public void PhaseChanged(CrawlPhase phase, CrawlStatsSnapshot stats)
    {
        _stats = stats;
        Render(force: true); // phase transitions are infrequent and worth showing immediately
    }

    /// <inheritdoc/>
    public void PageProcessed(string url, CrawlOutcome outcome, CrawlStatsSnapshot stats)
    {
        _stats = stats;
        _recent.Enqueue((url, outcome));
        while (_recent.Count > RecentCapacity) _recent.Dequeue();
        Render(force: false);
    }

    private void Render(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastRenderUtc < MinRedrawGap) return;
        _lastRenderUtc = now;
        _live.UpdateTarget(Build());
    }

    private IRenderable Build()
    {
        var s = _stats;

        // Progress bar: indexed / discovered. The denominator grows as links are found, so the bar
        // can dip when a page yields many new links and climbs back as they're crawled.
        double ratio = s.Discovered > 0 ? Math.Clamp((double)s.Indexed / s.Discovered, 0, 1) : 0;
        int filled = (int)Math.Round(ratio * BarWidth);
        string bar = $"[green]{new string('█', filled)}[/][grey37]{new string('░', BarWidth - filled)}[/]";
        string pct = s.Discovered > 0 ? $"{ratio * 100:0.0}%" : "—";
        var header = new Markup(
            $"[bold]{PhaseText(s.Phase)}[/]  {bar}  [bold]{s.Indexed}[/]/[bold]{s.Discovered}[/] ({pct})  [grey]{Elapsed(s.Elapsed)}[/]");

        var grid = new Grid();
        grid.AddColumn().AddColumn().AddColumn().AddColumn();
        grid.AddRow(Stat("Indexed", s.Indexed, "green"), Stat("Unchanged", s.Unchanged, "grey"), Stat("Redirect", s.Redirected, "blue"), Stat("NoIndex", s.NoIndex, "grey"));
        grid.AddRow(Stat("Skipped", s.SkippedType + s.SkippedSize, "yellow"), Stat("Gone", s.Gone, "red"), Stat("Disallowed", s.Disallowed, "darkorange"), Stat("Failed", s.Failed, "red"));
        grid.AddRow(Stat("Discovered", s.Discovered, "white"), Stat("Links", s.LinksFound, "white"), Stat("Removed", s.Removed, "red"), new Markup(" "));

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("[grey]outcome[/]").Width(11));
        table.AddColumn(new TableColumn("[grey]url[/]").NoWrap());
        foreach (var (url, outcome) in _recent.Reverse())
        {
            var (label, color) = OutcomeStyle(outcome);
            table.AddRow(new Markup($"[{color}]{label}[/]"), new Markup(Markup.Escape(Truncate(url, 100))));
        }

        return new Rows(header, new Markup(" "), grid, table);
    }

    private static IRenderable Stat(string label, long value, string color) =>
        new Markup($"[grey]{label}[/] [{color}]{value}[/]");

    private static string PhaseText(CrawlPhase phase) => phase switch
    {
        CrawlPhase.Starting => "[grey]Starting[/]",
        CrawlPhase.Crawling => "[green]Crawling[/]",
        CrawlPhase.RemovingBanned => "[darkorange]Removing banned[/]",
        CrawlPhase.Pruning => "[yellow]Pruning[/]",
        CrawlPhase.Optimizing => "[blue]Optimizing[/]",
        CrawlPhase.Completed => "[bold green]Completed[/]",
        CrawlPhase.Cancelled => "[bold red]Cancelled[/]",
        _ => phase.ToString(),
    };

    private static (string Label, string Color) OutcomeStyle(CrawlOutcome outcome) => outcome switch
    {
        CrawlOutcome.Indexed => ("indexed", "green"),
        CrawlOutcome.Unchanged => ("unchanged", "grey"),
        CrawlOutcome.NoIndex => ("noindex", "grey"),
        CrawlOutcome.SkippedType => ("skipped", "yellow"),
        CrawlOutcome.SkippedSize => ("skipped", "yellow"),
        CrawlOutcome.Redirected => ("redirect", "blue"),
        CrawlOutcome.Gone => ("gone", "red"),
        CrawlOutcome.Disallowed => ("disallowed", "darkorange"),
        CrawlOutcome.Failed => ("failed", "red"),
        _ => (outcome.ToString().ToLowerInvariant(), "white"),
    };

    private static string Elapsed(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}
