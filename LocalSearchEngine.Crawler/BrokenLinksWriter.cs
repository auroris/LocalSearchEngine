using System.Text;
using LocalSearchEngine.Core.Crawling.Reporting;

namespace LocalSearchEngine.Crawler;

/// <summary>
/// Writes the end-of-run broken-links report: in-scope links that returned 404/410 while crawling,
/// off-site links that failed the optional verification pass, and the hosts written off as
/// unreachable. The same data is also present (structured) in the JSON stats file; this is the
/// human-readable companion. Each broken link is listed with the page it was found on.
/// </summary>
internal static class BrokenLinksWriter
{
    /// <summary>Writes the broken-links report to <paramref name="path"/>.</summary>
    /// <param name="report">The completed crawl report.</param>
    /// <param name="path">Destination path for the text report.</param>
    /// <param name="externalChecked">Whether off-site link verification ran this crawl.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous write.</returns>
    public static Task WriteAsync(CrawlReport report, string path, bool externalChecked, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, Build(report, externalChecked), cancellationToken);

    private static string Build(CrawlReport report, bool externalChecked)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LocalSearchEngine broken-links report");
        sb.AppendLine("=====================================");
        sb.AppendLine($"Seed:                {report.SeedUrl}");
        sb.AppendLine($"Finished:            {report.FinishedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"External link check: {(externalChecked ? "enabled" : "disabled (in-scope 404s only)")}");
        sb.AppendLine();

        // On-site 404/410s first, then off-site failures; within each, grouped by the page they sit on.
        var links = report.BrokenLinks
            .OrderBy(b => b.External)
            .ThenBy(b => b.FoundOn ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();

        sb.AppendLine($"Broken links ({links.Count})");
        if (links.Count == 0)
        {
            sb.AppendLine("  none");
        }
        else
        {
            foreach (var link in links)
            {
                string tag = link.External ? $"{link.Reason}, external" : link.Reason;
                sb.AppendLine($"  {link.Url}");
                sb.AppendLine($"    [{tag}]  found on: {link.FoundOn ?? "—"}");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"Unreachable hosts ({report.UnreachableHosts.Count})");
        if (report.UnreachableHosts.Count == 0)
        {
            sb.AppendLine("  none");
        }
        else
        {
            foreach (var host in report.UnreachableHosts)
            {
                sb.AppendLine($"  {host}");
            }
        }

        return sb.ToString();
    }
}
