using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// Applies the crawl-state half of a finished <see cref="CrawlJob"/> — the CrawlState row, the LinkIndex
/// rows, and the destination link-status stamp — as one atomic transaction on the crawl's single write
/// connection. This is the crawler thread's only writer; the page's chunk writes are handed to the
/// <see cref="CrawlEmbedder"/> instead, which is what lets the crawl finish without waiting on embedding.
/// Every write takes the shared <see cref="DbWriteGate"/> so it never overlaps the embedder's chunk writes
/// (SQLite allows a single writer at a time). Each job is wrapped in its own try/catch so one bad page
/// can't halt the crawl.
/// </summary>
internal sealed class CrawlStateWriter
{
    /// <summary>The SQLite connection used to write crawl state and links; used only by the crawler thread.</summary>
    private readonly SqliteConnection _connection;
    /// <summary>The shared gate that serializes this writer against the embedder's chunk writes.</summary>
    private readonly DbWriteGate _gate;
    /// <summary>The logger instance.</summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrawlStateWriter"/> class.
    /// </summary>
    /// <param name="connection">The SQLite write connection (single-threaded: the crawler thread only).</param>
    /// <param name="gate">The shared write gate serializing against the embedder.</param>
    /// <param name="logger">The logger instance.</param>
    public CrawlStateWriter(SqliteConnection connection, DbWriteGate gate, ILogger logger)
    {
        _connection = connection;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>
    /// Persists the crawl-state and link-index rows implied by <paramref name="job"/> in one transaction —
    /// recorded against this URL's inbound links too, so even links on pages not re-parsed this run reflect
    /// its current status.
    /// </summary>
    /// <param name="job">The classified job to apply.</param>
    public async Task ApplyAsync(CrawlJob job)
    {
        try
        {
            using (await _gate.AcquireAsync())
            {
                using var tx = _connection.BeginTransaction();

                switch (job)
                {
                    case IndexJob j:
                        await CrawlStore.RecordCrawlStateAsync(_connection, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, j.DocKind, tx);
                        await CrawlStore.StoreLinksAsync(_connection, j.Url, j.Outlinks, j.OffsiteLinks, tx);
                        break;

                    case NoIndexJob j:
                        await CrawlStore.StoreLinksAsync(_connection, j.Url, j.Outlinks, j.OffsiteLinks, tx);
                        await CrawlStore.RecordCrawlStateAsync(_connection, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, j.DocKind, tx);
                        break;

                    case GoneJob j:
                        await CrawlStore.DeleteLinksAsync(_connection, j.Url, tx);
                        await CrawlStore.RecordVisitAsync(_connection, j.Url, j.StatusCode, clearMetadata: true, tx);
                        break;

                    case AliasJob j:
                        await CrawlStore.DeleteLinksAsync(_connection, j.Url, tx);
                        await CrawlStore.RecordVisitAsync(_connection, j.Url, j.StatusCode, clearMetadata: true, tx);
                        break;

                    case TouchJob j:
                        await CrawlStore.RecordVisitAsync(_connection, j.Url, j.StatusCode, clearMetadata: false, tx);
                        break;
                }

                await CrawlStore.UpdateLinkStatusByDestinationAsync(_connection, job.Url, (int)ClassifyLinkStatus(job), job.StatusCode, tx);

                await tx.CommitAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply crawl state for {Url}", job.Url);
        }
    }

    /// <summary>
    /// Classifies a crawl job into a <see cref="LinkStatus"/> based on status code and redirect context.
    /// </summary>
    /// <param name="job">The crawl job to classify.</param>
    /// <returns>The classified <see cref="LinkStatus"/>.</returns>
    private static LinkStatus ClassifyLinkStatus(CrawlJob job)
    {
        int code = job.StatusCode;
        if (code == 304 || code is >= 200 and < 300) return LinkStatus.Ok;
        if (code is >= 300 and < 400) return LinkStatus.Redirect;
        return LinkStatus.Error;
    }
}
