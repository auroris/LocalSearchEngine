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
/// single connection. The job's type selects the work — index or delete a page's chunks, record its
/// crawl state, store or clear its links, or simply stamp a visit — and after each job it records how
/// that destination last responded against the links that point at it, so even links on pages not
/// re-parsed this run reflect its current status. Each job is wrapped in its own try/catch so one bad
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

    /// <summary>
    /// Initializes a new instance of the <see cref="CrawlConsumer"/> class.
    /// </summary>
    /// <param name="connection">The SQLite database connection.</param>
    /// <param name="reader">The reader to consume crawl jobs from.</param>
    /// <param name="vectorSearchService">The vector search service for text indexing.</param>
    /// <param name="logger">The logger instance.</param>
    public CrawlConsumer(
        SqliteConnection connection,
        ChannelReader<CrawlJob> reader,
        VectorSearchService vectorSearchService,
        ILogger logger)
    {
        _connection = connection;
        _reader = reader;
        _vectorSearchService = vectorSearchService;
        _logger = logger;
    }

    /// <summary>
    /// Starts the background consumer loop, reading jobs from the channel and persisting them to SQLite and the Vector Search Service.
    /// </summary>
    public async Task ConsumeAsync()
    {
        await foreach (var job in _reader.ReadAllAsync())
        {
            try
            {
                if (job.RedirectSourceUrl != null)
                {
                    await _vectorSearchService.DeleteUrlChunksAsync(job.RedirectSourceUrl);
                    await CrawlStore.DeleteLinksAsync(_connection, job.RedirectSourceUrl, CancellationToken.None);
                    await CrawlStore.RecordVisitAsync(_connection, job.RedirectSourceUrl, 302, clearMetadata: true, CancellationToken.None);
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
                        await CrawlStore.RecordCrawlStateAsync(_connection, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, j.DocKind, CancellationToken.None);
                        await CrawlStore.StoreLinksAsync(_connection, j.Url, j.Outlinks, j.OffsiteLinks, CancellationToken.None);

                        break;

                    case NoIndexJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await CrawlStore.StoreLinksAsync(_connection, j.Url, j.Outlinks, j.OffsiteLinks, CancellationToken.None);
                        await CrawlStore.RecordCrawlStateAsync(_connection, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, j.DocKind, CancellationToken.None);
                        break;

                    case GoneJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await CrawlStore.DeleteLinksAsync(_connection, j.Url, CancellationToken.None);
                        await CrawlStore.RecordVisitAsync(_connection, j.Url, j.StatusCode, clearMetadata: true, CancellationToken.None);
                        break;

                    case AliasJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await CrawlStore.DeleteLinksAsync(_connection, j.Url, CancellationToken.None);
                        await CrawlStore.RecordVisitAsync(_connection, j.Url, j.StatusCode, clearMetadata: true, CancellationToken.None);
                        break;

                    case TouchJob j:
                        await CrawlStore.RecordVisitAsync(_connection, j.Url, j.StatusCode, clearMetadata: false, CancellationToken.None);
                        break;
                }

                // Record how this destination last responded against every link in the index that
                // points at it — so links to it (including from pages not re-parsed this run) reflect
                // its current status. The dequeued URL is the redirect source when a redirect was
                // followed, otherwise the job's own URL.
                var dequeuedUrl = job.RedirectSourceUrl ?? job.Url;
                await CrawlStore.UpdateLinkStatusByDestinationAsync(_connection, dequeuedUrl, (int)ClassifyLinkStatus(job), job.StatusCode, CancellationToken.None);
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
