using LocalSearchEngine.Core.Crawling.Reporting;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LocalSearchEngine.Crawler;

/// <summary>
/// Renders live crawl progress to an interactive terminal: a crawl progress bar (processed / discovered,
/// the denominator growing as the crawl finds links), a second embedding bar (embedded / queued, which
/// keeps moving after the crawl itself finishes while the backlog drains), a panel of running totals, and
/// a rolling list of the most recent URLs with how each resolved. The page and phase callbacks arrive on
/// the crawler thread while the embedding callback arrives on the embedder thread, so a single lock guards
/// the shared state and every redraw; updates are lightly throttled to keep redraws cheap.
/// </summary>
internal sealed class SpectreCrawlReporter : ICrawlReporter
{
    /// <summary>Maximum number of recent URL outcomes to display in the rolling list.</summary>
    private const int RecentCapacity = 10;
    /// <summary>Width in characters of the progress bars.</summary>
    private const int BarWidth = 40;
    /// <summary>Minimum time between consecutive redraws to avoid excessive CPU usage.</summary>
    private static readonly TimeSpan MinRedrawGap = TimeSpan.FromMilliseconds(50);

    private readonly LiveDisplayContext _live;
    private readonly object _gate = new();
    private readonly Queue<(string Url, CrawlOutcome Outcome)> _recent = new();
    private CrawlStatsSnapshot _stats;
    private int _embedProcessed;
    private int _embedQueued;
    private DateTime _lastRenderUtc = DateTime.MinValue;

    /// <summary>Initializes the reporter to drive the given live display.</summary>
    /// <param name="live">The Spectre live-display context the crawl runs inside.</param>
    public SpectreCrawlReporter(LiveDisplayContext live) => _live = live;

    /// <inheritdoc/>
    public void PhaseChanged(CrawlPhase phase, CrawlStatsSnapshot stats)
    {
        lock (_gate)
        {
            _stats = stats;
            Render(force: true); // phase transitions are infrequent and worth showing immediately
        }
    }

    /// <inheritdoc/>
    public void PageProcessed(string url, CrawlOutcome outcome, CrawlStatsSnapshot stats)
    {
        lock (_gate)
        {
            _stats = stats;
            _recent.Enqueue((url, outcome));
            while (_recent.Count > RecentCapacity) _recent.Dequeue();
            Render(force: false);
        }
    }

    /// <inheritdoc/>
    public void EmbedProgress(int processed, int queued)
    {
        lock (_gate)
        {
            _embedProcessed = processed;
            _embedQueued = queued;
            // Force the final tick (the backlog reaching the queue total) so the bar lands on 100%, since
            // no further callback would otherwise un-throttle it.
            Render(force: processed >= queued);
        }
    }

    /// <summary>Renders the current state to the display if the minimum redraw gap has elapsed or if forced.</summary>
    /// <remarks>Callers must hold <see cref="_gate"/>.</remarks>
    /// <param name="force">If true, bypasses the throttle and renders immediately.</param>
    private void Render(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastRenderUtc < MinRedrawGap) return;
        _lastRenderUtc = now;
        _live.UpdateTarget(Build());
    }

    /// <summary>Builds the visual component tree for the live display.</summary>
    /// <returns>A renderable Spectre.Console element.</returns>
    private IRenderable Build()
    {
        var s = _stats;

        // Crawl bar: processed / discovered. The denominator grows as links are found, so the bar can dip
        // when a page yields many new links and climbs back as they're crawled.
        double ratio = s.Discovered > 0 ? Math.Clamp((double)s.Processed / s.Discovered, 0, 1) : 0;
        var header = new Markup(
            $"[bold]{PhaseText(s.Phase)}[/]  {Bar(ratio, "green")}  [bold]{s.Processed}[/]/[bold]{s.Discovered}[/] ({Pct(ratio, s.Discovered)})  [grey]{Elapsed(s.Elapsed)}[/]");

        // Embedding bar: embedded / queued. The crawler races ahead, so this trails the crawl bar and then
        // catches up — continuing to advance after the crawl finishes, while the queued backlog drains.
        double embedRatio = _embedQueued > 0 ? Math.Clamp((double)_embedProcessed / _embedQueued, 0, 1) : 0;
        var embedHeader = new Markup(
            $"[bold aqua]Embedding[/]  {Bar(embedRatio, "aqua")}  [bold]{_embedProcessed}[/]/[bold]{_embedQueued}[/] ({Pct(embedRatio, _embedQueued)})");

        var grid = new Grid();
        grid.AddColumn().AddColumn().AddColumn().AddColumn();
        grid.AddRow(Stat("Indexed", s.Indexed, "green"), Stat("Unchanged", s.Unchanged, "grey"), Stat("Redirect", s.Redirected, "blue"), Stat("NoIndex", s.NoIndex, "grey"));
        grid.AddRow(Stat("Skipped", s.SkippedType + s.SkippedSize, "yellow"), Stat("Gone", s.Gone, "red"), Stat("Disallowed", s.Disallowed, "darkorange"), Stat("Failed", s.Failed, "red"));
        grid.AddRow(Stat("Discovered", s.Discovered, "white"), Stat("Links", s.LinksFound, "white"), Stat("Removed", s.Removed, "red"), Stat("Unreadable", s.LowQualityText, "yellow"));

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("[grey]outcome[/]").Width(11));
        table.AddColumn(new TableColumn("[grey]url[/]").NoWrap());
        foreach (var (url, outcome) in _recent.Reverse())
        {
            var (label, color) = OutcomeStyle(outcome);
            table.AddRow(new Markup($"[{color}]{label}[/]"), new Markup(Markup.Escape(Truncate(url, 100))));
        }

        return new Rows(header, embedHeader, new Markup(" "), grid, table);
    }

    /// <summary>Renders a fixed-width progress bar at the given fill ratio and color.</summary>
    private static string Bar(double ratio, string color)
    {
        int filled = (int)Math.Round(Math.Clamp(ratio, 0, 1) * BarWidth);
        return $"[{color}]{new string('█', filled)}[/][grey37]{new string('░', BarWidth - filled)}[/]";
    }

    /// <summary>Formats a percentage, or an em dash when there is nothing to measure against yet.</summary>
    private static string Pct(double ratio, long denominator) => denominator > 0 ? $"{ratio * 100:0.0}%" : "—";

    /// <summary>Formats a single statistic value with a label and color.</summary>
    private static IRenderable Stat(string label, long value, string color) =>
        new Markup($"[grey]{label}[/] [{color}]{value}[/]");

    /// <summary>Gets the display text and styling for a crawl phase.</summary>
    private static string PhaseText(CrawlPhase phase) => phase switch
    {
        CrawlPhase.Starting => "[grey]Starting[/]",
        CrawlPhase.Crawling => "[green]Crawling[/]",
        CrawlPhase.RemovingBanned => "[darkorange]Removing banned[/]",
        CrawlPhase.Pruning => "[yellow]Pruning[/]",
        CrawlPhase.Optimizing => "[blue]Optimizing[/]",
        CrawlPhase.CheckingLinks => "[aqua]Checking links[/]",
        CrawlPhase.Completed => "[bold green]Completed[/]",
        _ => phase.ToString(),
    };

    /// <summary>Gets the display label and color for a page processing outcome.</summary>
    private static (string Label, string Color) OutcomeStyle(CrawlOutcome outcome) => outcome switch
    {
        CrawlOutcome.Indexed => ("indexed", "green"),
        CrawlOutcome.Unchanged => ("unchanged", "grey"),
        CrawlOutcome.NoIndex => ("noindex", "grey"),
        CrawlOutcome.SkippedType => ("skipped", "yellow"),
        CrawlOutcome.SkippedSize => ("skipped", "yellow"),
        CrawlOutcome.LowQualityText => ("unreadable", "yellow"),
        CrawlOutcome.Redirected => ("redirect", "blue"),
        CrawlOutcome.Gone => ("gone", "red"),
        CrawlOutcome.Disallowed => ("disallowed", "darkorange"),
        CrawlOutcome.Failed => ("failed", "red"),
        _ => (outcome.ToString().ToLowerInvariant(), "white"),
    };

    /// <summary>Formats elapsed time as hh:mm:ss.</summary>
    private static string Elapsed(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>Truncates a string to a maximum length, appending an ellipsis if truncated.</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}
