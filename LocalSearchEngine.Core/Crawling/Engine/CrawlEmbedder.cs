using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.Crawling.Reporting;

namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>
/// The embedder half of the crawl, on its own thread behind an unbounded queue. For each URL the crawler
/// finishes it brings <c>text_chunks</c> into line: replacing a page's chunks (delete the old, embed its
/// text and headings on the CPU, upsert the new) or just clearing them when the page is no longer indexed.
/// The crawler never blocks on this — it drops work on the queue and moves on — so it finishes long before
/// the embedder grinds through the backlog. The expensive part (embedding) runs <em>outside</em> the shared
/// <see cref="DbWriteGate"/>; only the short delete/upsert takes the gate, so it barely contends with the
/// crawler's crawl-state writes. Each job is wrapped in its own try/catch so one bad page can't tear down
/// the loop, and the lane is marked <see cref="CrawlHeartbeat.Idle"/> whenever the queue is drained so the
/// watchdog never reads a finished job as a stall.
/// </summary>
internal sealed class CrawlEmbedder
{
    /// <summary>The reader of the unbounded queue the crawler fills.</summary>
    private readonly ChannelReader<EmbeddingJob> _reader;
    /// <summary>The vector search service that embeds text and writes/deletes chunks.</summary>
    private readonly VectorSearchService _vectorSearchService;
    /// <summary>The shared gate that serializes chunk writes against the crawler's crawl-state writes.</summary>
    private readonly DbWriteGate _gate;
    /// <summary>The logger instance.</summary>
    private readonly ILogger _logger;
    /// <summary>The shared activity marker; this thread bumps the embedder lane the watchdog reads.</summary>
    private readonly CrawlHeartbeat _heartbeat;
    /// <summary>The shared counts; the crawler bumps Queued, this thread bumps Processed as it drains.</summary>
    private readonly EmbeddingBacklog _backlog;
    /// <summary>Receives embedder progress for the live display's second bar.</summary>
    private readonly ICrawlReporter _reporter;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrawlEmbedder"/> class.
    /// </summary>
    /// <param name="reader">The reader of the unbounded embedding queue.</param>
    /// <param name="vectorSearchService">The vector search service for embedding and chunk writes.</param>
    /// <param name="gate">The shared write gate serializing against the crawler.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="heartbeat">The shared activity marker whose embedder lane this thread bumps.</param>
    /// <param name="backlog">The shared counts the crawler queues into and this thread drains.</param>
    /// <param name="reporter">The reporter that renders embedder progress (called from this thread).</param>
    public CrawlEmbedder(
        ChannelReader<EmbeddingJob> reader,
        VectorSearchService vectorSearchService,
        DbWriteGate gate,
        ILogger logger,
        CrawlHeartbeat heartbeat,
        EmbeddingBacklog backlog,
        ICrawlReporter reporter)
    {
        _reader = reader;
        _vectorSearchService = vectorSearchService;
        _gate = gate;
        _logger = logger;
        _heartbeat = heartbeat;
        _backlog = backlog;
        _reporter = reporter;
    }

    /// <summary>
    /// Runs the embedder loop until the queue is completed and fully drained, applying each item's chunk
    /// writes. Reads with <see cref="ChannelReader{T}.TryRead"/> so the lane can be marked idle while parked
    /// on an empty queue rather than leaving the last job's mark to look like a stall.
    /// </summary>
    public async Task ConsumeAsync()
    {
        while (true)
        {
            if (!_reader.TryRead(out var job))
            {
                _heartbeat.MarkEmbedder(CrawlHeartbeat.Idle);
                if (!await _reader.WaitToReadAsync()) break;
                continue;
            }

            _heartbeat.MarkEmbedder($"embedding {job.Url} (backlog {_backlog.Pending})");
            try
            {
                await ApplyAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply chunk writes for {Url}", job.Url);
            }

            // Count the item processed (success or logged failure) and report the new totals, so the
            // live display's embedding bar keeps moving even while the crawler is idle and the backlog drains.
            _backlog.RecordProcessed();
            _reporter.EmbedProgress(_backlog.Processed, _backlog.Queued);
        }

        _heartbeat.MarkEmbedder(CrawlHeartbeat.Idle);
    }

    /// <summary>
    /// Applies one item's chunk writes: embeds outside the gate, then takes the gate for the brief
    /// delete/upsert. Deletes precede upserts (and the redirect source is cleared first) so a re-crawl
    /// replaces rather than duplicates, exactly as the previous single consumer did.
    /// </summary>
    /// <param name="job">The chunk work to apply.</param>
    private async Task ApplyAsync(EmbeddingJob job)
    {
        // Embed first, off the gate — this is the minutes-long CPU work that must not block the crawler.
        IReadOnlyList<TextChunkRecord> textRecords = Array.Empty<TextChunkRecord>();
        IReadOnlyList<TextChunkRecord> headingRecords = Array.Empty<TextChunkRecord>();
        if (job.Action == ChunkAction.Replace)
        {
            textRecords = _vectorSearchService.BuildChunkRecords(job.Url, job.Text, isHeading: false);
            if (!string.IsNullOrWhiteSpace(job.Headings))
            {
                headingRecords = _vectorSearchService.BuildChunkRecords(job.Url, job.Headings, isHeading: true);
            }
        }

        using (await _gate.AcquireAsync())
        {
            if (job.RedirectSourceUrl != null)
            {
                await _vectorSearchService.DeleteUrlChunksAsync(job.RedirectSourceUrl);
            }
            if (job.Action != ChunkAction.None)
            {
                await _vectorSearchService.DeleteUrlChunksAsync(job.Url);
            }
            if (job.Action == ChunkAction.Replace)
            {
                await _vectorSearchService.UpsertChunkRecordsAsync(textRecords);
                await _vectorSearchService.UpsertChunkRecordsAsync(headingRecords);
            }
        }
    }
}
