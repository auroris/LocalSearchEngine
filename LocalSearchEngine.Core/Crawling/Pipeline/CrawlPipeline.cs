using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Searching;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>What one pipeline run produced, for the orchestrating facade's report and prune decision.</summary>
/// <param name="JobsSubmitted">Total classified jobs sent to the persistence consumer.</param>
/// <param name="IndexedCount">Pages that got an <see cref="IndexJob"/> this run.</param>
/// <param name="CappedWithWorkRemaining">Whether the maxPages cap cut the run off while the frontier still held work.</param>
/// <param name="HostCapSkipped">Whether any URL was skipped by the per-host cap (disables pruning, same as a capped run).</param>
internal sealed record PipelineResult(
    int JobsSubmitted, int IndexedCount, bool CappedWithWorkRemaining, bool HostCapSkipped);

/// <summary>
/// The crawl engine: two channels and the shared state between them. Documents flow through an
/// unbounded crawl channel into N workers; classified <see cref="CrawlJob"/>s flow through an
/// unbounded index channel into the single <see cref="PersistenceConsumer"/>. Both channels are
/// unbounded on purpose — workers are also producers, so a bounded crawl channel would deadlock the
/// moment every worker blocked writing children into a full queue, and the index channel must never
/// make a worker wait on the (much slower) embedder.
///
/// Termination is the <see cref="PendingWorkCounter"/> refcount, not channel emptiness: the worker
/// that decrements it to zero completes the crawl channel, the orchestrator completes the index
/// channel once all workers exit, and the run ends when the consumer drains. <see cref="Enqueue"/>
/// is the only frontier entrance, so the scope filter, the seen-claim, and the refcount can never
/// disagree with each other.
/// </summary>
internal sealed class CrawlPipeline
{
    private readonly Channel<Document> _crawlChannel = Channel.CreateUnbounded<Document>();
    private readonly Channel<CrawlJob> _indexChannel = Channel.CreateUnbounded<CrawlJob>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly HttpClient _httpClient;
    private readonly VectorSearchService _vectorSearchService;
    private readonly string _connectionString;
    private readonly SqliteConnection _writeConnection;
    private readonly ICrawlReporter _reporter;
    private readonly EmbeddingBacklog _backlog;

    private readonly object _indexDecisionGate = new();
    private readonly ConcurrentDictionary<string, int> _indexedPerHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _contentHashes = new(StringComparer.Ordinal);

    /// <summary>
    /// The most discovered (page-advertised) feeds one run will fetch. Legitimate sites advertise a
    /// handful — the main feed plus maybe per-section ones — while blog platforms advertise a
    /// distinct comments feed on every post; without a cap a large site would spend hundreds of
    /// fetches re-learning URLs it already knows.
    /// </summary>
    private const int MaxDiscoveredFeeds = 8;

    private int _jobsSubmitted;
    private int _indexedCount;
    private int _discoveredFeedBudget = MaxDiscoveredFeeds;
    private volatile bool _capped;
    private volatile bool _cappedWithWorkRemaining;
    private volatile bool _hostCapSkipped;

    public CrawlPipeline(
        CrawlPlan plan,
        HttpClient httpClient,
        VectorSearchService vectorSearchService,
        string connectionString,
        SqliteConnection writeConnection,
        RobotsDirectory robots,
        HostHealthTracker hostHealth,
        ICrawlObserver observer,
        CrawlHeartbeat heartbeat,
        ICrawlReporter reporter,
        EmbeddingBacklog backlog,
        ILogger logger)
    {
        Plan = plan;
        var seedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seedUri in plan.SeedUris)
        {
            seedKeys.Add(UrlNormalizer.Normalize(seedUri));
        }
        SeedKeys = seedKeys;
        _httpClient = httpClient;
        _vectorSearchService = vectorSearchService;
        _connectionString = connectionString;
        _writeConnection = writeConnection;
        Robots = robots;
        HostHealth = hostHealth;
        Observer = observer;
        Heartbeat = heartbeat;
        _reporter = reporter;
        _backlog = backlog;
        Logger = logger;
        Downloader = new PageDownloader(httpClient, logger);
    }

    public CrawlPlan Plan { get; }

    /// <summary>Gets the seeds' normalized identities — the only URLs whose off-scope redirects widen the crawl scope.</summary>
    public IReadOnlySet<string> SeedKeys { get; }

    /// <summary>Gets the crawl-wide seen set (also the live "discovered" count for reporting).</summary>
    public VisitedSet Visited { get; } = new();

    internal PendingWorkCounter Pending { get; } = new();
    internal HostGate Gate { get; } = new();
    internal RobotsDirectory Robots { get; }
    internal HostHealthTracker HostHealth { get; }
    internal ICrawlObserver Observer { get; }
    internal CrawlHeartbeat Heartbeat { get; }
    internal ILogger Logger { get; }
    internal PageDownloader Downloader { get; }

    /// <summary>Gets a value indicating whether the maxPages cap has tripped; workers drain-skip once it has.</summary>
    internal bool Capped => _capped;

    internal int IndexedCount => Volatile.Read(ref _indexedCount);

    internal int IndexedOn(string host) => _indexedPerHost.TryGetValue(host, out var n) ? n : 0;

    /// <summary>Records that the cap skipped real work, which forfeits the run's "completed naturally" status.</summary>
    internal void NoteCappedSkip() => _cappedWithWorkRemaining = true;

    /// <summary>Records that the per-host cap skipped a URL, which disables pruning for the run.</summary>
    internal void NoteHostCapSkip() => _hostCapSkipped = true;

    /// <summary>
    /// The single frontier entrance: scope first (so a URL rejected before a seed redirect widens
    /// scope doesn't burn its dedup key), then the atomic seen-claim, then the refcount, then the
    /// channel write. Only called while the caller holds pending work (seeding or a document in
    /// flight), which is why the write can never race channel completion.
    /// </summary>
    /// <param name="document">The work item to offer.</param>
    /// <returns><c>true</c> if the document entered the frontier.</returns>
    internal bool Enqueue(Document document)
    {
        if (!Plan.Scope.IsAllowed(document.FetchUri)) return false;
        if (!Visited.TryMarkSeen(document.DedupKey)) return false;
        Pending.Increment();
        _crawlChannel.Writer.TryWrite(document);
        return true;
    }

    /// <summary>
    /// Sends an already-classified job to the persistence consumer and keeps the submitted-job and
    /// embedding-backlog tallies. An <see cref="IndexJob"/>'s slot must already have been reserved
    /// through <see cref="TryAcceptIndex"/>.
    /// </summary>
    /// <param name="job">The job to persist.</param>
    internal void Submit(CrawlJob job)
    {
        Interlocked.Increment(ref _jobsSubmitted);
        if (job is not TouchJob)
        {
            _backlog.RecordQueued();
        }
        _indexChannel.Writer.TryWrite(job);
    }

    /// <summary>
    /// Makes the atomic final decision for a newly extracted index candidate. Same-run duplicate
    /// ownership and the global index-slot reservation share one lock so an alias can only point at
    /// a URL that already owns a real slot, while distinct pages can never overshoot
    /// <see cref="CrawlPlan.MaxPages"/>.
    /// </summary>
    /// <param name="contentHash">The candidate's extracted-content hash.</param>
    /// <param name="url">The candidate URL.</param>
    /// <param name="duplicateOf">Receives the accepted same-run owner when this is a duplicate;
    /// otherwise <c>null</c>, including when the cap rejected the candidate.</param>
    /// <returns><c>true</c> when the candidate owns a reserved index slot.</returns>
    internal bool TryAcceptIndex(string contentHash, string url, out string? duplicateOf)
    {
        lock (_indexDecisionGate)
        {
            if (_contentHashes.TryGetValue(contentHash, out var owner))
            {
                duplicateOf = string.Equals(owner, url, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : owner;
                return false;
            }

            if (_indexedCount >= Plan.MaxPages)
            {
                duplicateOf = null;
                _capped = true;
                _cappedWithWorkRemaining = true;
                return false;
            }

            _contentHashes.Add(contentHash, url);
            _indexedCount++;
            if (Uri.TryCreate(url, UriKind.Absolute, out var indexedUri))
            {
                _indexedPerHost.AddOrUpdate(indexedUri.Host, 1, (_, n) => n + 1);
            }
            if (_indexedCount >= Plan.MaxPages)
            {
                _capped = true;
            }

            duplicateOf = null;
            return true;
        }
    }

    /// <summary>
    /// Offers a page-advertised feed to the frontier under the per-run budget. Take-then-refund so
    /// the budget counts feeds that will actually be fetched: a duplicate or out-of-scope feed
    /// (rejected at the choke point) hands its token back.
    /// </summary>
    /// <param name="feedUri">The exact resolved feed target.</param>
    /// <returns><c>true</c> if the feed entered the frontier.</returns>
    internal bool TryDiscoverFeed(Uri feedUri)
    {
        if (Interlocked.Decrement(ref _discoveredFeedBudget) < 0)
        {
            Interlocked.Increment(ref _discoveredFeedBudget);
            return false;
        }
        if (!Enqueue(new FeedDocument(feedUri)))
        {
            Interlocked.Increment(ref _discoveredFeedBudget);
            return false;
        }
        Logger.LogInformation("Consulting advertised feed {Url} as extra seed material.", UrlNormalizer.Normalize(feedUri));
        return true;
    }

    /// <summary>Releases one unit of pending work; the release that strikes zero completes the crawl channel.</summary>
    internal void ReleasePending()
    {
        if (Pending.Decrement())
        {
            _crawlChannel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Runs the crawl to completion: start the consumer and workers, seed under the root pending
    /// token (so a crawl whose seeds all dedup away still terminates), wait for the workers to drain
    /// the frontier, then complete the index channel and wait for the consumer. A seeding failure
    /// still shuts the run down in order before rethrowing — abandoned workers on a never-completed
    /// channel would otherwise leak as a hang.
    /// </summary>
    /// <param name="ct">Cancels the run.</param>
    /// <returns>The run's tallies for the facade's report and prune decision.</returns>
    public async Task<PipelineResult> RunAsync(CancellationToken ct = default)
    {
        var consumer = new PersistenceConsumer(
            _indexChannel.Reader, _writeConnection, _vectorSearchService, _backlog, _reporter, Heartbeat, Logger);
        var consumerTask = consumer.ConsumeAsync();

        int workerCount = Math.Max(1, Plan.CrawlWorkers);
        var connections = new SqliteConnection[workerCount + 1]; // one per worker + one for seeding
        try
        {
            for (int i = 0; i < connections.Length; i++)
            {
                connections[i] = new SqliteConnection(_connectionString);
                await connections[i].OpenAsync(ct);
            }

            var workerTasks = new Task[workerCount];
            for (int i = 0; i < workerCount; i++)
            {
                var worker = new CrawlWorker(this, new PipelineContext(this, connections[i]), $"worker-{i + 1}");
                workerTasks[i] = worker.RunAsync(_crawlChannel.Reader, ct);
            }

            var seedContext = new PipelineContext(this, connections[^1]);
            Exception? seedFailure = null;
            Pending.Increment();
            try
            {
                foreach (var source in Plan.SeedSources)
                {
                    await source.SeedAsync(seedContext, ct);
                }
            }
            catch (Exception ex)
            {
                seedFailure = ex;
            }
            finally
            {
                ReleasePending();
            }

            await Task.WhenAll(workerTasks);
            _indexChannel.Writer.TryComplete();
            await consumerTask;

            if (seedFailure is not null)
            {
                ExceptionDispatchInfo.Capture(seedFailure).Throw();
            }

            return new PipelineResult(
                Volatile.Read(ref _jobsSubmitted), IndexedCount, _cappedWithWorkRemaining, _hostCapSkipped);
        }
        finally
        {
            foreach (var connection in connections)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }
}
