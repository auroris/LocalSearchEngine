using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalSearchEngine.Core.Crawling.Reporting;

namespace LocalSearchEngine.Crawler;

/// <summary>
/// Writes the end-of-run <see cref="CrawlReport"/> to disk in two forms: a machine-readable JSON
/// document and a human-readable text summary.
/// </summary>
internal static class CrawlStatsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes the report as both JSON and text.</summary>
    /// <param name="report">The crawl report to serialize.</param>
    /// <param name="jsonPath">Destination path for the JSON document.</param>
    /// <param name="textPath">Destination path for the text summary.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous write.</returns>
    public static async Task WriteAsync(CrawlReport report, string jsonPath, string textPath, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(textPath, BuildText(report), cancellationToken);
    }

    private static string BuildText(CrawlReport r)
    {
        var s = r.Stats;
        string status = r.Cancelled ? "cancelled" : r.CompletedNaturally ? "completed" : "stopped (capped)";

        var sb = new StringBuilder();
        sb.AppendLine("LocalSearchEngine crawl report");
        sb.AppendLine("==============================");
        sb.AppendLine($"Seed:      {r.SeedUrl}");
        sb.AppendLine($"Started:   {r.StartedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Finished:  {r.FinishedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Duration:  {r.Duration:hh\\:mm\\:ss}");
        sb.AppendLine($"Status:    {status}");
        sb.AppendLine();
        sb.AppendLine("Pages");
        sb.AppendLine($"  Indexed       {s.Indexed}");
        sb.AppendLine($"  Unchanged     {s.Unchanged}");
        sb.AppendLine($"  NoIndex       {s.NoIndex}");
        sb.AppendLine($"  Skipped       {s.SkippedType + s.SkippedSize}  (type {s.SkippedType}, size {s.SkippedSize})");
        sb.AppendLine($"  Redirected    {s.Redirected}");
        sb.AppendLine($"  Gone          {s.Gone}");
        sb.AppendLine($"  Disallowed    {s.Disallowed}");
        sb.AppendLine($"  Failed        {s.Failed}");
        sb.AppendLine($"  Processed     {s.Processed}");
        sb.AppendLine();
        sb.AppendLine("Links");
        sb.AppendLine($"  Found         {s.LinksFound}");
        sb.AppendLine($"  Unique        {s.Discovered}");
        sb.AppendLine();
        sb.AppendLine("Index");
        sb.AppendLine($"  Items added   {r.ItemsAdded}");
        sb.AppendLine($"  Items deleted {r.ItemsDeleted}  (gone {s.Gone}, banned {s.RemovedBanned}, stale {s.RemovedStale})");
        sb.AppendLine($"  Indexed URLs in DB   {r.IndexedUrlsInDb}");
        sb.AppendLine($"  Crawl-state rows     {r.CrawlStateRowsInDb}");
        return sb.ToString();
    }
}
