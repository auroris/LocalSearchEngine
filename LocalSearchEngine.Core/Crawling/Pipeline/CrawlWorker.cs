using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// One of the N concurrent crawl loops. For each dequeued document it runs the page gauntlet
/// (per-host cap, host health, robots, conditional-request state), takes the host's politeness
/// gate, fetches once, resolves the transport outcome uniformly, and only hands successful bodies
/// to the document's typed processing — which runs while the host gate is still held, so each
/// host's documents stay exactly sequential. Every path through the loop releases the document's
/// pending-work token in a finally; the release that strikes zero completes the crawl channel and
/// ends the crawl. One bad page never ends the loop: failures classify as a touch (or a log line
/// for infrastructure fetches) and the worker moves on.
/// </summary>
internal sealed class CrawlWorker
{
    /// <summary>The synthetic status stamped on a redirect source's crawl-state row. The real 3xx code
    /// isn't visible once the client has followed the chain, so all redirect sources record a plain 302.</summary>
    private const int RedirectStatusCode = 302;

    private readonly CrawlPipeline _pipeline;
    private readonly PipelineContext _ctx;
    private readonly string _lane;

    public CrawlWorker(CrawlPipeline pipeline, PipelineContext ctx, string lane)
    {
        _pipeline = pipeline;
        _ctx = ctx;
        _lane = lane;
    }

    /// <summary>
    /// Drains the crawl channel until it completes. Reads with TryRead so the worker's heartbeat
    /// lane can be marked idle while parked on an empty channel, rather than leaving the last
    /// document's mark to look like a stall.
    /// </summary>
    /// <param name="reader">The crawl channel.</param>
    /// <param name="ct">Cancels the loop.</param>
    public async Task RunAsync(ChannelReader<Document> reader, CancellationToken ct)
    {
        while (true)
        {
            if (!reader.TryRead(out var document))
            {
                _pipeline.Heartbeat.Mark(_lane, CrawlHeartbeat.Idle);
                if (!await reader.WaitToReadAsync(ct)) break;
                continue;
            }

            try
            {
                if (_pipeline.Capped)
                {
                    // The maxPages cap tripped with this document still queued: drain it without
                    // processing. The skip is what forfeits "completed naturally", exactly like the
                    // old engine exiting its loop with a non-empty queue.
                    _pipeline.NoteCappedSkip();
                }
                else
                {
                    await ProcessAsync(document, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A cancelled run is being abandoned, not completed; rethrow after the release below.
                throw;
            }
            catch (Exception ex)
            {
                if (document.IsPage)
                {
                    _pipeline.Observer.OnFetchError(ex, document.DedupKey);
                    _pipeline.Submit(new TouchJob(document.DedupKey, 500));
                }
                else
                {
                    _pipeline.Logger.LogWarning(ex, "Infrastructure fetch failed for {Url}", document.DedupKey);
                }
            }
            finally
            {
                // Unconditional: every dequeued document releases exactly one pending token, no
                // matter how processing ended — this is the crawl's termination correctness.
                _pipeline.ReleasePending();
            }
        }

        _pipeline.Heartbeat.Mark(_lane, CrawlHeartbeat.Idle);
    }

    private async Task ProcessAsync(Document document, CancellationToken ct)
    {
        var host = document.FetchUri.Host;
        string? condETag = null;
        string? condLastModified = null;
        string? priorContentHash = null;
        var robots = RobotsRules.AllowAll; // infrastructure fetches skip robots; pages overwrite below

        if (document.IsPage)
        {
            if (_pipeline.IndexedOn(host) >= _pipeline.Plan.MaxPagesPerHost)
            {
                _pipeline.NoteHostCapSkip();
                _pipeline.Observer.OnHostCapReached(_pipeline.Plan.MaxPagesPerHost, host, document.DedupKey);
                return;
            }
            if (_pipeline.HostHealth.IsUnreachable(host))
            {
                return;
            }

            _pipeline.Heartbeat.Mark(_lane, $"preparing {document.DedupKey}");
            _pipeline.Observer.OnPageFetching(_pipeline.IndexedCount, _pipeline.Visited.Count, document.DedupKey);

            robots = await _pipeline.Robots.GetOrFetchAsync(document.FetchUri);

            // The robots fetch was this host's first contact; it may just have been written off.
            if (_pipeline.HostHealth.IsUnreachable(host))
            {
                return;
            }
            if (!CrawlPolicy.IsAllowedByRobots(document.DedupKey, robots))
            {
                _pipeline.Observer.OnPageDisallowed(document.DedupKey);
                return;
            }

            var state = await CrawlStore.GetCrawlStateAsync(_ctx.Read, document.DedupKey);
            priorContentHash = state.ContentHash;

            // A user noindex rule requires a body every run. An HTML row from before link-context
            // indexing also needs one unconditional response so anchor and nearby editorial text
            // can be backfilled; once parsed, its stored version restores normal cheap 304s.
            bool needsLinkContextBackfill = state.DocKind == DocKind.Html
                && state.LinkContextVersion < CrawlStore.CurrentLinkContextVersion;
            bool suppressConditional = _pipeline.Plan.NoIndexRules.Matches(document.DedupKey)
                || needsLinkContextBackfill;
            condETag = suppressConditional ? null : state.ETag;
            condLastModified = suppressConditional ? null : state.LastModified;
        }
        else if (_pipeline.HostHealth.IsUnreachable(host))
        {
            return;
        }

        var minGap = HostGate.ResolveDelay(robots, _pipeline.Plan.DefaultRequestDelayMs);
        _pipeline.Heartbeat.Mark(_lane, $"{CrawlHeartbeat.PoliteWaitPrefix} for {host}");
        using (await _pipeline.Gate.EnterAsync(host, minGap, ct))
        {
            // Authoritative cap re-check: same-host work serializes on the gate, so an index that
            // pushed the host over its cap while this document queued is only visible now.
            if (document.IsPage && _pipeline.IndexedOn(host) >= _pipeline.Plan.MaxPagesPerHost)
            {
                _pipeline.NoteHostCapSkip();
                _pipeline.Observer.OnHostCapReached(_pipeline.Plan.MaxPagesPerHost, host, document.DedupKey);
                return;
            }

            _pipeline.Heartbeat.Mark(_lane, $"fetching {document.DedupKey}");
            var download = await _pipeline.Downloader.DownloadAsync(
                document.FetchUri, document.DedupKey, condETag, condLastModified,
                _pipeline.Plan.MaxCrawlSizeBytes, acceptAnyContentType: !document.IsPage);

            if (document.IsPage)
            {
                await HandlePageOutcomeAsync(document, priorContentHash, download, ct);
            }
            else
            {
                await HandleInfrastructureOutcomeAsync(document, download, ct);
            }
        }
    }

    /// <summary>Resolves a page fetch's transport outcome; only a supported success body reaches typed processing.</summary>
    private async Task HandlePageOutcomeAsync(Document document, string? priorContentHash, DownloadResult download, CancellationToken ct)
    {
        var url = document.DedupKey;
        int statusCode = (int)download.StatusCode;

        switch (download.Status)
        {
            case DownloadStatus.NotModified:
                _pipeline.Observer.OnPageUnchanged(url);
                if (_pipeline.Plan.FollowLinks)
                {
                    await EnqueueStoredOutlinksAsync(url);
                }
                _pipeline.Submit(new TouchJob(url, statusCode));
                break;

            case DownloadStatus.Redirected:
                HandleRedirect(document, download.FinalRequestUri);
                break;

            case DownloadStatus.Gone:
                _pipeline.Observer.OnPageGone(url, statusCode);
                _pipeline.Submit(new GoneJob(url, statusCode));
                break;

            case DownloadStatus.Failed:
                _pipeline.Observer.OnPageFailed(url, statusCode);
                _pipeline.Submit(new TouchJob(url, statusCode));
                break;

            case DownloadStatus.SizeLimitExceeded:
                _pipeline.Observer.OnPageSkippedSize(url, download.SizeRead, _pipeline.Plan.MaxCrawlSizeBytes);
                _pipeline.Submit(new TouchJob(url, statusCode));
                break;

            case DownloadStatus.UnsupportedType:
                _pipeline.Observer.OnPageSkippedType(url, download.ContentType);
                _pipeline.Submit(new TouchJob(url, statusCode));
                break;

            case DownloadStatus.Success:
                var fetch = FetchResult.FromSuccess(download, priorContentHash);
                var typed = document is PageDocument page ? DocumentFactory.Resolve(page, fetch) : document;
                if (typed is null)
                {
                    _pipeline.Observer.OnPageSkippedType(url, download.ContentType);
                    _pipeline.Submit(new TouchJob(url, statusCode));
                }
                else
                {
                    await typed.ProcessAsync(fetch, _ctx, ct);
                }
                break;

            default:
                throw new InvalidOperationException($"Unhandled download status: {download.Status}");
        }
    }

    /// <summary>
    /// Handles a request that redirected: the target is treated exactly like a discovered link —
    /// offered to the frontier, which deduplicates it and runs the same gauntlet on its own turn —
    /// and the source is recorded as a redirect, dropping any content, links, and chunks it used to
    /// have. An off-scope target is reported but not followed; the seed redirecting off its origin
    /// is the one case that widens the scope, so the destination (a vanity domain pointing at the
    /// real site) is adopted and crawled.
    /// </summary>
    private void HandleRedirect(Document document, Uri? finalUri)
    {
        // A redirect result always carries its target; fall back to a plain touch if it somehow doesn't.
        if (finalUri is null)
        {
            _pipeline.Submit(new TouchJob(document.DedupKey, RedirectStatusCode));
            return;
        }

        var target = UrlNormalizer.Normalize(finalUri);

        if (_pipeline.Plan.Scope.IsAllowed(finalUri))
        {
            _pipeline.Observer.OnPageRedirected(document.DedupKey, target);
            _pipeline.Enqueue(new PageDocument(finalUri));
        }
        else if (_pipeline.SeedKeys.Contains(document.DedupKey))
        {
            _pipeline.Observer.OnSeedRedirectedToNewOrigin(document.DedupKey, UrlOrigin.Key(finalUri));
            _pipeline.Plan.Scope.AddOrigin(finalUri);
            _pipeline.Enqueue(new PageDocument(finalUri));
        }
        else
        {
            _pipeline.Observer.OnPageRedirectedOutScope(document.DedupKey, target);
        }

        _pipeline.Submit(new AliasJob(document.DedupKey, RedirectStatusCode));
    }

    /// <summary>
    /// Re-enqueues the outlinks stored for an unchanged (304) page, so pages reachable only through
    /// it are still reached without re-parsing a body the server didn't send.
    /// </summary>
    private async Task EnqueueStoredOutlinksAsync(string url)
    {
        try
        {
            var links = await CrawlStore.GetStoredOutlinksAsync(_ctx.Read, url);
            int added = 0;
            foreach (var link in links)
            {
                if (Uri.TryCreate(link, UriKind.Absolute, out var linkUri) && _ctx.Discover(linkUri))
                {
                    added++;
                }
            }
            if (added > 0)
            {
                _pipeline.Logger.LogDebug("Re-enqueued {Count} stored outlinks from unchanged page {Url}", added, url);
            }
        }
        catch (Exception ex)
        {
            _pipeline.Logger.LogWarning(ex, "Failed to read stored outlinks for unchanged page {Url}; subsequent pages may not be reached.", url);
        }
    }

    /// <summary>
    /// Resolves a non-page (sitemap/feed) fetch: successes parse, redirects re-enqueue the same
    /// document type at the target, and everything else is just a log line — infrastructure fetches
    /// produce no jobs and no page events, exactly as the old sitemap service stayed invisible to
    /// the stats.
    /// </summary>
    private async Task HandleInfrastructureOutcomeAsync(Document document, DownloadResult download, CancellationToken ct)
    {
        switch (download.Status)
        {
            case DownloadStatus.Success:
                await document.ProcessAsync(FetchResult.FromSuccess(download, null), _ctx, ct);
                break;

            case DownloadStatus.Redirected:
                if (download.FinalRequestUri is Uri target)
                {
                    // A feed is the update run's seed, so give its canonical redirect the same
                    // trust boundary as a full crawl's root redirect. This covers common
                    // http -> https and apex -> www canonicalization and anchors item scope at
                    // the feed's actual origin.
                    if (document is FeedDocument)
                    {
                        _pipeline.Observer.OnSeedRedirectedToNewOrigin(
                            document.DedupKey, UrlOrigin.Key(target));
                        _pipeline.Plan.Scope.AddOrigin(target);
                    }
                    _pipeline.Enqueue(document.WithLocation(target));
                }
                break;

            default:
                _pipeline.Logger.LogDebug("Skipping {Url}: {Status} ({Code})",
                    document.DedupKey, download.Status, (int)download.StatusCode);
                break;
        }
    }
}
