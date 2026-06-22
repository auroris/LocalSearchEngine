using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// The consumer half of the crawl. It reads <see cref="CrawlJob"/>s off the channel the producer fills
/// and applies each to the database, so every index and crawl-state write funnels through one task on a
/// single connection. Each job runs in two ordered stages: first the vector-store chunk writes (which
/// the sqlite-vec connector applies on its own connections), then a single transaction over all the
/// crawl-state and link-index writes — recorded against this destination's inbound links too, so even
/// links on pages not re-parsed this run reflect its current status. Doing the vector writes first is
/// required: SQLite allows a single writer, so they must complete before the transaction takes the
/// write lock, and a kill between the two stages is self-healing because the next crawl re-fetches and
/// the content-hash/has-chunks check reconciles. Each job is wrapped in its own try/catch so one bad
/// page can't tear down the whole consumer.
/// </summary>
internal sealed class CrawlConsumer
{
    /// <summary>The SQLite connection used to write crawl state and links.</summary>
    private readonly SqliteConnection _connection;
    /// <summary>The channel reader to receive crawl jobs from the producer.</summary>
    private readonly ChannelReader<CrawlJob> _reader;
    /// <summary>The vector search service to index or delete page content embeddings.</summary>
    private readonly VectorSearchService _vectorSearchService;
    /// <summary>The logger instance.</summary>
    private readonly ILogger _logger;
    /// <summary>The shared activity marker, bumped before each job so the watchdog can see consumer stalls.</summary>
    private readonly CrawlHeartbeat _heartbeat;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrawlConsumer"/> class.
    /// </summary>
    /// <param name="connection">The SQLite database connection.</param>
    /// <param name="reader">The reader to consume crawl jobs from.</param>
    /// <param name="vectorSearchService">The vector search service for text indexing.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="heartbeat">The shared activity marker the orchestrator's stall watchdog reads.</param>
    public CrawlConsumer(
        SqliteConnection connection,
        ChannelReader<CrawlJob> reader,
        VectorSearchService vectorSearchService,
        ILogger logger,
        CrawlHeartbeat heartbeat)
    {
        _connection = connection;
        _reader = reader;
        _vectorSearchService = vectorSearchService;
        _logger = logger;
        _heartbeat = heartbeat;
    }

    /// <summary>
    /// Starts the background consumer loop, reading jobs from the channel and persisting them to SQLite and the Vector Search Service.
    /// </summary>
    public async Task ConsumeAsync()
    {
        await foreach (var job in _reader.ReadAllAsync())
        {
            _heartbeat.Mark($"indexing {job.Url}");
            try
            {
                // Stage 1 — vector-store chunk writes. These go through the connector's own connections,
                // so they can't enlist in the transaction below; they must finish before it opens or the
                // connector would hit SQLITE_BUSY against the held single-writer lock.
                if (job.RedirectSourceUrl != null)
                {
                    await _vectorSearchService.DeleteUrlChunksAsync(job.RedirectSourceUrl);
                }
                switch (job)
                {
                    case IndexJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await _vectorSearchService.IndexUrlChunksAsync(j.Url, j.Text, isHeading: false);
                        if (!string.IsNullOrWhiteSpace(j.Headings))
                        {
                            await _vectorSearchService.IndexUrlChunksAsync(j.Url, j.Headings, isHeading: true);
                        }
                        break;
                    case NoIndexJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        break;
                    case GoneJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        break;
                    case AliasJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        break;
                    // TouchJob keeps the existing index, so it has no chunk writes.
                }

                // Stage 2 — crawl-state and link-index writes, applied as one atomic unit so a kill
                // mid-job can't leave a torn multi-row write.
                using var tx = _connection.BeginTransaction();

                if (job.RedirectSourceUrl != null)
                {
                    await CrawlStore.DeleteLinksAsync(_connection, job.RedirectSourceUrl, tx);
                    await CrawlStore.RecordVisitAsync(_connection, job.RedirectSourceUrl, 302, clearMetadata: true, tx);
                }

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

                // Record how this destination last responded against every link in the index that
                // points at it — so links to it (including from pages not re-parsed this run) reflect
                // its current status. The dequeued URL is the redirect source when a redirect was
                // followed, otherwise the job's own URL.
                var dequeuedUrl = job.RedirectSourceUrl ?? job.Url;
                await CrawlStore.UpdateLinkStatusByDestinationAsync(_connection, dequeuedUrl, (int)ClassifyLinkStatus(job), job.StatusCode, tx);

                await tx.CommitAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply crawl result for {Url}", job.Url);
            }
        }
    }

    /// <summary>
    /// Classifies a crawl job into a <see cref="LinkStatus"/> based on status code and redirect context.
    /// </summary>
    /// <param name="job">The crawl job to classify.</param>
    /// <returns>The classified <see cref="LinkStatus"/>.</returns>
    private static LinkStatus ClassifyLinkStatus(CrawlJob job)
    {
        if (job.RedirectSourceUrl != null) return LinkStatus.Redirect;
        int code = job.StatusCode;
        if (code == 304 || code is >= 200 and < 300) return LinkStatus.Ok;
        if (code is >= 300 and < 400) return LinkStatus.Redirect;
        return LinkStatus.Error;
    }
}
