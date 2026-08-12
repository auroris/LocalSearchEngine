using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Storage;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// One unit of crawl work flowing through the crawl channel, and the polymorphic behavior for the
/// content behind it. A document carries two forms of its URL on purpose: <see cref="FetchUri"/> is
/// the exact resolved target and is what goes on the wire (re-parsing a normalized string re-encodes
/// percent-escapes, which is how encoded filesystem paths used to 404), while <see cref="DedupKey"/>
/// is the normalized display form that is the URL's identity everywhere else — the visited set, the
/// database rows, robots matching. Keeping the stored form identical to the old engine's means an
/// existing database keeps deduplicating, 304-validating, and pruning with no migration.
///
/// Construction is trivial and side-effect free — no I/O, no dedup checks, no channel writes; a
/// constructor can't be async, can't say "never mind", and can't re-type itself, so all of that
/// lives in the worker and in <see cref="ProcessAsync"/>. Contract for implementations: any child
/// work must be enqueued (via <see cref="ICrawlContext.Discover"/>/<see cref="ICrawlContext.Enqueue"/>)
/// before ProcessAsync returns — the completion refcount treats the document's own pending token as
/// covering its children's enqueues, and a late enqueue would race crawl termination.
/// </summary>
internal abstract class Document
{
    /// <summary>Gets the exact URI the HTTP request uses.</summary>
    public Uri FetchUri { get; }

    /// <summary>Gets the URL's normalized identity: the dedup key and the stored database form.</summary>
    public string DedupKey { get; }

    protected Document(Uri fetchUri)
    {
        FetchUri = fetchUri;
        DedupKey = UrlNormalizer.Normalize(fetchUri);
    }

    /// <summary>
    /// Gets a value indicating whether this document is a page of the site (as opposed to crawl
    /// infrastructure like sitemaps and feeds). Pages get the full gauntlet — robots Disallow,
    /// conditional GET from stored state, a CrawlState row, page observer events. Infrastructure
    /// fetches skip all of that: they aren't content, a robots-declared sitemap is authorized by
    /// the declaration itself, and a row for them would make them prune candidates.
    /// </summary>
    public virtual bool IsPage => true;

    /// <summary>
    /// Processes a successfully fetched body: extract, spawn child work, and submit the page's
    /// classification. Transport outcomes never reach here — the worker resolves them uniformly.
    /// </summary>
    /// <param name="fetch">The successful download.</param>
    /// <param name="ctx">The worker's crawl context.</param>
    /// <param name="ct">Cancels processing.</param>
    public abstract Task ProcessAsync(FetchResult fetch, ICrawlContext ctx, CancellationToken ct);

    /// <summary>
    /// Re-targets this document at a new location, preserving its type and baggage. Used when an
    /// infrastructure fetch (sitemap, feed) redirects: the target must be fetched as the same kind
    /// of document. Pages never take this path — their redirects run through the worker's redirect
    /// handling, which records the source as an alias.
    /// </summary>
    /// <param name="fetchUri">The redirect target.</param>
    /// <returns>An equivalent document at the new location.</returns>
    public virtual Document WithLocation(Uri fetchUri) => new PageDocument(fetchUri);

    /// <summary>
    /// Finishes an extracted, indexable document — the shared tail of the HTML, PDF, and DOCX paths.
    /// Hashes what would be embedded and uses it to short-circuit the expensive step: a
    /// <see cref="TouchJob"/> when this URL's stored content is unchanged (and its chunks actually
    /// exist — a row without chunks is a torn state that must self-heal by re-embedding), an
    /// <see cref="AliasJob"/> when the same content is already indexed under another URL, otherwise
    /// an <see cref="IndexJob"/> that (re-)embeds.
    /// </summary>
    /// <param name="fetch">The successful download.</param>
    /// <param name="ctx">The worker's crawl context.</param>
    /// <param name="title">The extracted title.</param>
    /// <param name="headings">The extracted heading text.</param>
    /// <param name="text">The extracted main text.</param>
    /// <param name="outlinks">The in-scope outlinks (normalized), for the link rows.</param>
    /// <param name="offsiteLinks">The off-site links, for optional verification.</param>
    /// <param name="kind">The classified document kind.</param>
    /// <param name="linkEvidence">Anchor and nearby text describing HTML outlinks; empty for non-HTML documents.</param>
    private protected async Task EmitIndexableAsync(
        FetchResult fetch, ICrawlContext ctx,
        string? title, string headings, string text,
        IReadOnlyCollection<string> outlinks, IReadOnlyCollection<string> offsiteLinks, DocKind kind,
        IReadOnlyCollection<LinkEvidence>? linkEvidence = null)
    {
        linkEvidence ??= Array.Empty<LinkEvidence>();
        string contentHash = ComputeContentHash(title, headings, text);

        // The fallback for servers that ignore ETag/If-Modified-Since and answer 200 with an
        // unchanged page: if what we'd embed is identical to what's already indexed, skip the
        // re-embed and just stamp the visit. Hashing the extracted text rather than raw bytes is
        // what makes this reliable — per-request markup noise (CSP nonces, timestamps) no longer
        // forces a needless re-index. Outlinks were already discovered by the caller, so the
        // frontier is unaffected by returning early here.
        if (fetch.PriorContentHash == contentHash
            && await CrawlStore.UrlHasChunksAsync(ctx.Read, DedupKey))
        {
            ctx.Observer.OnPageUnchangedHash(DedupKey);
            ctx.Submit(new TouchJob(
                DedupKey, fetch.StatusCode, title, outlinks, offsiteLinks, linkEvidence));
            return;
        }

        // Prior-run duplicates live in the database; same-run duplicates can only be caught by the
        // in-memory decision, because this run's chunk writes may still be queued behind the
        // embedder. The final in-memory decision couples duplicate ownership to an exact global
        // index-slot reservation, so an alias always points at an accepted owner and concurrent
        // workers cannot overshoot maxPages.
        string? duplicateOf = await CrawlStore.FindIndexedDuplicateAsync(ctx.Read, contentHash, DedupKey);
        if (duplicateOf is null
            && !ctx.TryAcceptIndex(contentHash, DedupKey, out duplicateOf)
            && duplicateOf is null)
        {
            // The global cap rejected this distinct candidate. It deliberately produces no visit
            // job: a capped run is partial and must leave the prior stored entry untouched.
            return;
        }
        if (duplicateOf != null)
        {
            ctx.Observer.OnPageDuplicateContent(DedupKey, duplicateOf);
            if (Uri.TryCreate(duplicateOf, UriKind.Absolute, out var duplicateUri))
            {
                ctx.Enqueue(new PageDocument(duplicateUri));
            }
            ctx.Submit(new AliasJob(DedupKey, fetch.StatusCode));
            return;
        }

        ctx.Observer.OnPageIndexed(DedupKey, outlinks.Count);
        ctx.Submit(new IndexJob(DedupKey, fetch.StatusCode, title, headings, text,
            fetch.ETag, fetch.LastModified, contentHash, outlinks, offsiteLinks, linkEvidence, kind));
    }

    /// <summary>
    /// Hashes the extracted, indexable fields of a page — what actually gets embedded — rather than
    /// the raw response bytes. The fields are domain-separated so that moving text between the
    /// title, headings, and body changes the hash. This is the basis for both the unchanged-page
    /// shortcut and cross-URL duplicate detection, so two responses hash the same exactly when they
    /// would produce the same index entry.
    /// </summary>
    /// <param name="title">The extracted title, if any.</param>
    /// <param name="headings">The extracted heading text.</param>
    /// <param name="text">The extracted main text.</param>
    /// <returns>The uppercase hex SHA-256 of the combined fields.</returns>
    private protected static string ComputeContentHash(string? title, string headings, string text)
    {
        var canonical = string.Concat(title, "\n", headings, "\n", text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>
/// The "we don't know what this is yet" work item — what links, sitemap entries, and feed items
/// enqueue. The worker fetches it once and resolves it through <see cref="DocumentFactory"/> into
/// the typed document that actually processes the body, so type detection costs no second fetch.
/// </summary>
internal sealed class PageDocument : Document
{
    public PageDocument(Uri fetchUri) : base(fetchUri) { }

    /// <summary>Never called: the worker resolves a PageDocument to a typed document before processing.</summary>
    public override Task ProcessAsync(FetchResult fetch, ICrawlContext ctx, CancellationToken ct) =>
        throw new InvalidOperationException($"PageDocument for {DedupKey} was processed without factory resolution.");
}
