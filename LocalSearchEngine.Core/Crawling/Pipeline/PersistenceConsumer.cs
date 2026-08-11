using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;
using LocalSearchEngine.Core.Searching;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// The single consumer of the index channel, and with it the run's only database writer — that
/// exclusivity, not a gate or a busy-timeout, is what keeps SQLite's one-writer rule intact while
/// four workers crawl. Anything else that writes (the post-crawl prune, robots-ban removal, link
/// verification stamps) must run only after this consumer has been awaited.
///
/// Per job it applies two independently-guarded halves, because a chunk failure must not roll back
/// the crawl-state row (the row surviving a failed embed is what lets the next crawl notice
/// hash-unchanged-but-chunkless and self-heal by re-embedding):
/// first the crawl-state transaction (rows exactly as the old writer applied them), then the chunk
/// work (embed on the CPU, delete old chunks, upsert new — delete-before-upsert is what makes a
/// re-crawl replace instead of duplicate). The heavy embedding happens with nothing else contending
/// for the database, so the "indexer parallelism of one" is also the run's write serialization.
/// </summary>
internal sealed class PersistenceConsumer
{
    /// <summary>The heartbeat lane this consumer marks.</summary>
    public const string LaneName = "persistence";

    private enum ChunkAction
    {
        /// <summary>Chunks untouched (a plain visit stamp).</summary>
        None,
        /// <summary>The URL no longer holds indexed content; its chunks are removed.</summary>
        Delete,
        /// <summary>The URL's content is (re-)embedded and its chunks replaced.</summary>
        Replace,
    }

    private readonly ChannelReader<CrawlJob> _reader;
    private readonly SqliteConnection _write;
    private readonly VectorSearchService _vectorSearchService;
    private readonly EmbeddingBacklog _backlog;
    private readonly ICrawlReporter _reporter;
    private readonly CrawlHeartbeat _heartbeat;
    private readonly ILogger _logger;

    public PersistenceConsumer(
        ChannelReader<CrawlJob> reader,
        SqliteConnection write,
        VectorSearchService vectorSearchService,
        EmbeddingBacklog backlog,
        ICrawlReporter reporter,
        CrawlHeartbeat heartbeat,
        ILogger logger)
    {
        _reader = reader;
        _write = write;
        _vectorSearchService = vectorSearchService;
        _backlog = backlog;
        _reporter = reporter;
        _heartbeat = heartbeat;
        _logger = logger;
    }

    /// <summary>
    /// Runs until the index channel is completed and fully drained. Reads with TryRead so the lane
    /// can be marked idle while parked on an empty queue rather than leaving the last job's mark to
    /// look like a stall. Each job's halves wrap their own try/catch, so one bad page never tears
    /// down the loop and every queued item is drained.
    /// </summary>
    public async Task ConsumeAsync()
    {
        while (true)
        {
            if (!_reader.TryRead(out var job))
            {
                _heartbeat.Mark(LaneName, CrawlHeartbeat.Idle);
                if (!await _reader.WaitToReadAsync()) break;
                continue;
            }

            _heartbeat.Mark(LaneName, $"persisting {job.Url} (backlog {_backlog.Pending})");
            try
            {
                await ApplyStateAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply crawl state for {Url}", job.Url);
            }

            var action = ChunkActionFor(job);
            if (action != ChunkAction.None)
            {
                try
                {
                    await ApplyChunksAsync(job, action);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to apply chunk writes for {Url}", job.Url);
                }

                // Count the item processed (success or logged failure) and report the new totals, so
                // the live display's embedding bar keeps moving while the queued backlog drains.
                _backlog.RecordProcessed();
                _reporter.EmbedProgress(_backlog.Processed, _backlog.Queued);
            }
        }

        _heartbeat.Mark(LaneName, CrawlHeartbeat.Idle);
    }

    /// <summary>
    /// Persists the crawl-state and link-index rows implied by the job in one transaction — recorded
    /// against the URL's inbound links too, so links on pages not re-parsed this run still reflect
    /// its current status.
    /// </summary>
    private async Task ApplyStateAsync(CrawlJob job)
    {
        using var tx = _write.BeginTransaction();

        switch (job)
        {
            case IndexJob j:
                await CrawlStore.RecordCrawlStateAsync(_write, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, j.DocKind, tx);
                await CrawlStore.StoreLinksAsync(_write, j.Url, j.Outlinks, j.OffsiteLinks, tx);
                break;

            case NoIndexJob j:
                await CrawlStore.StoreLinksAsync(_write, j.Url, j.Outlinks, j.OffsiteLinks, tx);
                await CrawlStore.RecordCrawlStateAsync(_write, j.Url, j.StatusCode, j.ETag, j.LastModified, j.Title, j.ContentHash, j.DocKind, tx);
                break;

            case GoneJob:
            case AliasJob:
                // A URL that no longer holds content of its own: 404/410 (gone) or a
                // redirect/canonical/alias. Same cleanup either way; the status code differentiates.
                await CrawlStore.DeleteLinksAsync(_write, job.Url, tx);
                await CrawlStore.RecordVisitAsync(_write, job.Url, job.StatusCode, clearMetadata: true, tx);
                break;

            case TouchJob j:
                await CrawlStore.RecordVisitAsync(_write, j.Url, j.StatusCode, clearMetadata: false, tx);
                break;
        }

        await CrawlStore.UpdateLinkStatusByDestinationAsync(_write, job.Url, (int)ClassifyLinkStatus(job), job.StatusCode, tx);

        await tx.CommitAsync();
    }

    /// <summary>Applies the job's chunk consequence: embed-and-replace for an index, delete for everything that unindexes.</summary>
    private async Task ApplyChunksAsync(CrawlJob job, ChunkAction action)
    {
        IReadOnlyList<TextChunkRecord> textRecords = Array.Empty<TextChunkRecord>();
        IReadOnlyList<TextChunkRecord> headingRecords = Array.Empty<TextChunkRecord>();
        if (action == ChunkAction.Replace && job is IndexJob index)
        {
            textRecords = _vectorSearchService.BuildChunkRecords(index.Url, index.Text, isHeading: false);
            if (!string.IsNullOrWhiteSpace(index.Headings))
            {
                headingRecords = _vectorSearchService.BuildChunkRecords(index.Url, index.Headings, isHeading: true);
            }
        }

        await _vectorSearchService.DeleteUrlChunksAsync(job.Url);
        if (action == ChunkAction.Replace)
        {
            await _vectorSearchService.UpsertChunkRecordsAsync(textRecords);
            await _vectorSearchService.UpsertChunkRecordsAsync(headingRecords);
        }
    }

    /// <summary>Maps a job to its chunk consequence: index replaces, noindex/gone/alias delete, touch leaves chunks alone.</summary>
    private static ChunkAction ChunkActionFor(CrawlJob job) => job switch
    {
        IndexJob => ChunkAction.Replace,
        NoIndexJob or GoneJob or AliasJob => ChunkAction.Delete,
        _ => ChunkAction.None,
    };

    /// <summary>Classifies a job into the <see cref="LinkStatus"/> stamped on its inbound links.</summary>
    private static LinkStatus ClassifyLinkStatus(CrawlJob job)
    {
        int code = job.StatusCode;
        if (code == 304 || code is >= 200 and < 300) return LinkStatus.Ok;
        if (code is >= 300 and < 400) return LinkStatus.Redirect;
        return LinkStatus.Error;
    }
}
