# Configuration Guide

**LocalSearchEngine** is highly configurable through the shared `appsettings.json` file located in the solution root, as well as command-line arguments.

## Shared Configuration (`appsettings.json`)

To avoid duplicating settings, both the Crawler and Web projects share a single `appsettings.json` file at the root of the repository.

### Configuration Load Order (Hierarchy & Overrides)

Both projects resolve configurations in the following priority order, with later sources overriding earlier ones:
1. **`../appsettings.json`** (Lowest priority): The shared root configuration file.
2. **`./appsettings.json`** (Medium priority): Project-specific configuration files in the executing directory, useful for project-specific overrides.
3. **`appsettings.{Environment}.json`** (Medium-High priority): Environment-specific files (e.g. `appsettings.Development.json` for Web).
4. **Environment Variables & Command-Line Arguments** (Highest priority).

---

## Crawler Settings

The crawler's behaviors, bounds, and scope restrictions can be set in JSON format:

```json
{
  "db": "search.db",
  "max-pages": null,
  "max-pages-per-host": null,
  "max-crawl-size-bytes": 15728640,
  "request-delay-ms": 250,
  "allowed-servers": [
    "example.com",
    "https://wiki.example.org:8080"
  ],
  "noindex-patterns": [
    "*/tag/*",
    "https://example.com/calendar/*"
  ],
  "check-external-links": false
}
```

### Settings Reference

* **`db`** (string, default: `"search.db"`): Path to output SQLite database file containing state and index data. Can be overridden using CLI argument `--db <path>`.
* **`max-pages`** (integer/null, default: `null`): A hard cap on how many successful pages the crawler will index this run. Stops the crawl once reached.
* **`max-pages-per-host`** (integer/null, default: `null`): Maximum pages to download from a single origin. Useful to prevent infinite crawl loops on calendar or search filter pages.
* **`max-crawl-size-bytes`** (integer, default: `15728640` / 15MB): The maximum size (after decompression) of an individual document or page. Large files are ignored or terminated mid-stream.
* **`request-delay-ms`** (integer, default: `250`): The default politeness delay in milliseconds between requests to the same host when `robots.txt` does not specify a `Crawl-delay`. Set to `0` to disable the delay. Can be overridden using CLI argument `--request-delay-ms <n>`.
* **`allowed-servers`** (array of strings): Defines the scope of servers the crawler is permitted to visit. See **Crawl Scope Rules** below.
* **`noindex-patterns`** (array of strings): URL glob patterns whose pages are crawled for their links but never indexed ("noindex, follow"). See **Noindex Patterns** below.
* **`check-external-links`** (boolean, default: `false`): If `true`, the crawler will verify off-site links found on crawled pages (by performing a lightweight `HEAD` or `GET` request) without indexing their content.
* **`feed`** (boolean, default: `false`): If `true`, the URL is treated as an RSS/Atom feed and the run is a **feed-driven update** instead of a full crawl. Only the items the feed lists are fetched (with conditional requests, so unchanged items answer `304` and are never re-embedded), links are not followed, and nothing the run didn't visit is pruned — the feed is trusted as the site's change journal. Site deletions are reconciled by the next full crawl. Can also be enabled per run with the CLI flag `--feed`.

  Full crawls also consult feeds automatically: when a page advertises one via `<link rel="alternate" type="application/rss+xml">` (or `atom+xml`), its items are added to the frontier as extra seed material — a **positive indicator only**. Everything a feed lists is guaranteed to be visited (even posts nothing links to yet), but a URL's *absence* from a feed never causes it to be skipped, since feeds are windows onto the newest items. A small per-run budget (8 discovered feeds) keeps platforms that advertise a comments feed on every post from wasting fetches.
* **`allow-incremental`** (boolean, default: `false`): Lets a site's advertised feed **bound the whole run**. The CLI flag `--full` overrides it for a single run — a deliberate full recrawl after feed problems, suspected drift, or deletions the journal has already rotated out. Before crawling, the site's root page is probed for its advertised feed, and the feed's entries are walked newest-first: entries not yet covered are the changes to crawl, and the first entry whose stored crawl time is at or after the entry's own date proves everything older was already seen — so the run crawls exactly those changes and stops (a run where nothing changed costs three requests per site). If any site can't prove its change list complete — no advertised feed, entries without dates, or a feed whose window ends before an already-covered entry — the run falls back to a normal full crawl, on the assumption that there may be changes the feed no longer lists. Incremental runs are partial: they never prune and never remove robots-banned URLs; full crawls reconcile those. Can also be enabled per run with the CLI flag `--allow-incremental`.

  For autodiscovery to engage on your own site, the feed must be advertised on the root page, dated (`pubDate` / `updated`), and list *every* change — including edits to old pages, re-listed with a fresh date.
* **`incremental-feed`** (string URL): A **declared change journal** that replaces per-site autodiscovery when `allow-incremental` is on. One feed vouches for *every* configured host at once — the configuration for site sets where only one host can serve a feed (a bare document server has no page to advertise one on). The same boundary rule applies: entries above the first already-covered one are the whole run's changes; a feed that ends without a covered entry forces a full crawl.

  The recommended journal shape is *everything changed in the window, plus a tail of the most recently changed items before it* (e.g. the last 24 hours plus the next ten most recent). That makes the feed self-anchoring: on a quiet day the tail bounds the run immediately (three requests total), and if the crawler misses enough runs that the whole tail looks new, the full-crawl fallback triggers on its own. Give the window margin over your crawl interval, and list deletions too — a listed URL that answers 404 is removed from the index.
* **`crawl-workers`** and no-argument runs: running the crawler with **no `<url>`** seeds every entry in `allowed-servers` and crawls them in a single run (entries without a scheme are seeded over `http`). Combined with `allow-incremental: true`, a bare scheduled `crawler.exe` run does the right thing every time: incremental when the feeds prove it, full when they can't.
* **`crawl-workers`** (integer, default: `4`): Number of concurrent crawl workers. Each host is still fetched sequentially with the politeness delay, so extra workers pay off when the crawl spans several hosts. Can be overridden using CLI argument `--crawl-workers <n>`.

---

## Crawl Scope Rules

By default, the **seed URL** (the starting URL passed to the crawler CLI) defines the primary scope. Its exact origin—scheme, host, and port—is always allowed.

To expand this scope to other servers, add patterns to the `allowed-servers` list:

| Pattern | Allowed Scope Examples |
| --- | --- |
| `example.com` | Any scheme, any port (e.g. `http://example.com`, `https://example.com:8080`) |
| `https://example.com` | HTTPS only, any port (e.g. `https://example.com`, `https://example.com:3000`) |
| `example.com:8080` | Any scheme, port 8080 only (e.g. `http://example.com:8080`, `https://example.com:8080`) |
| `http://example.com:8080` | Exactly that origin only |

> [!WARNING]
> Subdomains and host variants (like `www.`) are **not** implied. If a website is served on both `example.com` and `www.example.com`, you must list both separately.

---

## Noindex Patterns

Sometimes a page is worth crawling for the links it contains but is not worth putting in the search index itself — think tag clouds, paginated archives, calendars, or print views. Listing a URL glob pattern in `noindex-patterns` makes the crawler treat any matching page as **"noindex, follow"**: its outlinks are still discovered and followed, but its own content never enters the index. This is the configuration-side equivalent of a page declaring `noindex` through a `<meta name="robots">` tag or an `X-Robots-Tag` header.

Patterns are matched (case-insensitively) against the **whole normalized URL**, using the same glob style as `robots.txt`:

* `*` matches any run of characters.
* A trailing `$` anchors the end of the URL.
* Every other character is literal, and the pattern is anchored at the start.

| Pattern | Matches |
| --- | --- |
| `*/tag/*` | Any URL containing a `/tag/` path segment (`https://example.com/tag/news`) |
| `https://example.com/calendar/*` | Everything under that section, on HTTPS |
| `*://wiki.example.com/*` | Every page on a host you want followed but not indexed |
| `*?replytocom=*` | URLs carrying a particular query parameter |
| `https://example.com/about$` | Exactly that one URL (the `$` prevents matching `/about-us`) |

> [!NOTE]
> A pattern with no `*` and no trailing `$` matches any URL it is a *prefix* of, so `https://example.com/about` also matches `https://example.com/about-us`. Add a trailing `$` when you mean an exact URL.

> [!NOTE]
> Like pages that declare `noindex` themselves, pages matched here do not count toward `max-pages` or `max-pages-per-host` (those caps count indexed pages). Listing a pattern never causes a URL to be fetched — it only changes whether a fetched page is indexed.

---

## Web Search Settings

The web app configures the hybrid vector and keyword search ranker using the `SearchSettings` block:

```json
{
  "db": "search.db",
  "SearchSettings": {
    "CandidatePoolSize": 500,
    "MaxDistance": 0.6,
    "ReciprocalRankConstant": 60,
    "SemanticWeight": 1.0,
    "KeywordWeight": 1.0,
    "InboundLinkWeight": 0.75,
    "InboundContextBoost": 0.2,
    "InboundSourceAuthorityWeight": 0.25,
    "AuthorityWeight": 0.2,
    "ExactPhraseBoost": 0.35,
    "ProximityBoost": 0.15,
    "HeadingBoost": 0.3,
    "TitleBoost": 0.35,
    "FilenameBoost": 0.4,
    "TermInTextBoost": 0.2,
    "MultiChunkBoost": 0.1,
    "NonHtmlPenalty": 0.15
  }
}
```

### Ranking Weights Reference

The searcher retrieves independent semantic, page-text, and inbound-link candidate pools. The page-text pool uses an FTS5 OR query ordered by BM25, so useful partial matches are not excluded merely because one query term is absent. An additional adjacent-phrase pass can rescue phrase matches outside that broad pool. The inbound pool searches anchor text, nearby source prose, the nearest section heading, and the source-page title with different BM25 field weights. The re-ranker deduplicates all streams by target URL, combines their URL ranks using reciprocal-rank fusion, and then applies bounded lexical, structural, and authority features.

* **`CandidatePoolSize`** (integer): The maximum number of candidates fetched by each semantic, broad page-text BM25, phrase-rescue, and inbound-link retrieval pass before URL deduplication.
* **`MaxDistance`** (double): The cosine distance threshold for vector matches, from `0.0` (identical direction) through `1.0` (orthogonal) to a theoretical `2.0` (opposite). Lower limits require closer semantic matches.
* **`ReciprocalRankConstant`** (double): Controls how quickly reciprocal-rank contributions decay. Larger values reduce the difference between nearby ranks; `60` is a conventional starting value.
* **`SemanticWeight`** (double): Weight of the semantic URL rank in reciprocal-rank fusion.
* **`KeywordWeight`** (double): Weight of the BM25 URL rank in reciprocal-rank fusion.
* **`InboundLinkWeight`** (double): Weight of the inbound anchor/context URL rank in reciprocal-rank fusion. Set to `0` to prevent inbound descriptions from contributing a rank stream.
* **`InboundContextBoost`** (double): Maximum bounded bonus for query-term coverage, proximity, and phrase quality in the target's best matching inbound-link description.
* **`InboundSourceAuthorityWeight`** (double): How strongly the referring page's normalized authority refines otherwise similar inbound BM25 matches. At `0.25`, authority changes the match magnitude by at most 25 percent.
* **`AuthorityWeight`** (double): Maximum query-independent bonus from the target page's normalized internal PageRank. It is intentionally small so authority breaks close relevance decisions instead of replacing relevance.
* **`ExactPhraseBoost`** (double): Bounded bonus when the query's terms occur adjacently and in order. This is not a hard tier.
* **`ProximityBoost`** (double): Maximum bonus for query terms appearing in a tight token window. Partial coverage and wider spans receive less.
* **`HeadingBoost`** (double): Bonus applied if query keywords match headings (`<h1>`, `<h2>`, etc.) in the document.
* **`TitleBoost`** (double): Maximum coverage-weighted bonus for query terms in the document title.
* **`FilenameBoost`** (double): Maximum coverage-weighted bonus for query terms in the URL's final filename or slug.
* **`TermInTextBoost`** (double): Maximum bonus for distinct query-term coverage in matching chunks.
* **`MultiChunkBoost`** (double): Maximum corroboration bonus when one URL has several distinct matching chunks; additional chunks have diminishing returns.
* **`NonHtmlPenalty`** (double): Soft penalty for PDF and DOCX results. Strong documents can still outrank weaker HTML pages.

### Link-Signal Backfill After Upgrading

An existing database already contains the internal link graph, so PageRank can be computed immediately. It does not contain the new anchor and surrounding-context records. Each older HTML row is therefore fetched once without its stored ETag or Last-Modified validator when the crawler next reaches it; unchanged visible text still skips re-embedding, and normal conditional requests resume afterward.

Run one full crawl after upgrading if you want link context populated for the entire existing index. An incremental or feed-driven run only backfills the pages it visits. The backfill respects the usual robots, scope, page-size, and politeness rules.
