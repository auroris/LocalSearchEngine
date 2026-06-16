using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Reads crawl jobs from the channel and persists indexing changes and visit states to the database.
/// </summary>
internal sealed class CrawlConsumer
{
    private readonly SqliteConnection _connection;
    private readonly ChannelReader<CrawlJob> _reader;
    private readonly VectorSearchService _vectorSearchService;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrawlConsumer"/> class.
    /// </summary>
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
                        await CrawlStore.RecordCrawlStateAsync(_connection, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, CancellationToken.None);
                        await CrawlStore.StoreLinksAsync(_connection, j.Url, j.Outlinks, j.OffsiteLinks, CancellationToken.None);

                        break;

                    case NoIndexJob j:
                        await _vectorSearchService.DeleteUrlChunksAsync(j.Url);
                        await CrawlStore.StoreLinksAsync(_connection, j.Url, j.Outlinks, j.OffsiteLinks, CancellationToken.None);
                        await CrawlStore.RecordCrawlStateAsync(_connection, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, CancellationToken.None);
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

    private static LinkStatus ClassifyLinkStatus(CrawlJob job)
    {
        if (job.RedirectSourceUrl != null) return LinkStatus.Redirect;
        int code = job.StatusCode;
        if (code == 304 || code is >= 200 and < 300) return LinkStatus.Ok;
        if (code is >= 300 and < 400) return LinkStatus.Redirect;
        return LinkStatus.Error;
    }
}
