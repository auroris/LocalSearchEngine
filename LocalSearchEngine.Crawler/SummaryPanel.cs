using LocalSearchEngine.Core.Crawling.Reporting;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LocalSearchEngine.Crawler;

/// <summary>
/// Builds the boxed end-of-run summary printed to the console after a crawl, echoing the headline
/// figures from the <see cref="CrawlReport"/> and pointing at the files that were written.
/// </summary>
internal static class SummaryPanel
{
    /// <summary>Builds the summary panel.</summary>
    /// <param name="report">The completed crawl report.</param>
    /// <param name="statsJsonPath">Where the JSON stats were written.</param>
    /// <param name="statsTextPath">Where the text stats were written.</param>
    /// <param name="logPath">Where the log file was written.</param>
    /// <param name="brokenLinksPath">Where the broken-links report was written.</param>
    /// <returns>A renderable panel for <c>AnsiConsole.Write</c>.</returns>
    public static IRenderable Build(CrawlReport report, string statsJsonPath, string statsTextPath, string logPath, string brokenLinksPath)
    {
        var s = report.Stats;
        string status = report.Cancelled ? "[bold red]cancelled[/]"
            : report.CompletedNaturally ? "[bold green]completed[/]"
            : "[bold yellow]stopped (capped)[/]";

        var grid = new Grid();
        grid.AddColumn().AddColumn();
        grid.AddRow("[grey]Status[/]", status);
        grid.AddRow("[grey]Duration[/]", $"{report.Duration:hh\\:mm\\:ss}");
        grid.AddRow("[grey]Indexed[/]", $"[green]{s.Indexed}[/]  ([grey]added[/] {report.ItemsAdded})");
        grid.AddRow("[grey]Unchanged[/]", s.Unchanged.ToString());
        grid.AddRow("[grey]Skipped / NoIndex[/]", $"{s.SkippedType + s.SkippedSize} / {s.NoIndex}");
        grid.AddRow("[grey]Unreadable PDFs[/]", s.LowQualityText.ToString());
        grid.AddRow("[grey]Redirected[/]", s.Redirected.ToString());
        grid.AddRow("[grey]Gone / Disallowed / Failed[/]", $"{s.Gone} / {s.Disallowed} / {s.Failed}");
        grid.AddRow("[grey]Removed[/]", $"[red]{report.ItemsDeleted}[/]  ([grey]banned[/] {s.RemovedBanned}, [grey]stale[/] {s.RemovedStale})");
        grid.AddRow("[grey]Links found / unique[/]", $"{s.LinksFound} / {s.Discovered}");
        string brokenColor = report.BrokenLinks.Count > 0 ? "red" : "grey";
        string redirectedColor = report.RedirectedLinks.Count > 0 ? "yellow" : "grey";
        grid.AddRow("[grey]Broken / redirected / unreachable[/]", $"[{brokenColor}]{report.BrokenLinks.Count}[/] / [{redirectedColor}]{report.RedirectedLinks.Count}[/] / {report.UnreachableHosts.Count}");
        grid.AddRow("[grey]Indexed URLs in DB[/]", report.IndexedUrlsInDb.ToString());
        grid.AddRow("[grey]Crawl-state rows[/]", report.CrawlStateRowsInDb.ToString());
        grid.AddEmptyRow();
        grid.AddRow("[grey]Stats[/]", Markup.Escape(statsJsonPath));
        grid.AddRow("[grey]     [/]", Markup.Escape(statsTextPath));
        grid.AddRow("[grey]Broken[/]", Markup.Escape(brokenLinksPath));
        grid.AddRow("[grey]Log[/]", Markup.Escape(logPath));

        return new Panel(grid)
            .Header("[bold]Crawl summary[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }
}
