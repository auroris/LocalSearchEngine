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
* **`allow-incremental`** (boolean, default: `false`): Lets a site's advertised feed **bound the whole run**. Before crawling, the site's root page is probed for its advertised feed, and the feed's entries are walked newest-first: entries not yet covered are the changes to crawl, and the first entry whose stored crawl time is at or after the entry's own date proves everything older was already seen — so the run crawls exactly those changes and stops (a run where nothing changed costs three requests per site). If any site can't prove its change list complete — no advertised feed, entries without dates, or a feed whose window ends before an already-covered entry — the run falls back to a normal full crawl, on the assumption that there may be changes the feed no longer lists. Incremental runs are partial: they never prune and never remove robots-banned URLs; full crawls reconcile those. Can also be enabled per run with the CLI flag `--allow-incremental`.

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
    "ExactPhraseBoost": 0.5,
    "AndTermsBoost": 0.25,
    "HeadingBoost": 0.3,
    "TitleBoost": 0.35,
    "FilenameBoost": 0.4,
    "TermInTextBoost": 0.2
  }
}
```

### Ranking Weights Reference

The hybrid search ranks pages by combining vector search cosine similarity with keyword matching. The score begins at `0.0` and accumulates weight based on these properties:

* **`CandidatePoolSize`** (integer): The number of initial semantic vector match candidates fetched from `sqlite-vec` before sorting and applying keyword and structural boosting.
* **`MaxDistance`** (double): The cosine distance threshold (from `0.0` to `1.0`) for vector matches. Lower limits require matches to be more semantically identical to the search query.
* **`ExactPhraseBoost`** (double): Score bonus added when the user's query matches an exact phrase in the document text verbatim.
* **`AndTermsBoost`** (double): Score bonus added when all terms of the search query appear somewhere in the document.
* **`HeadingBoost`** (double): Bonus applied if query keywords match headings (`<h1>`, `<h2>`, etc.) in the document.
* **`TitleBoost`** (double): Bonus applied if query keywords match the title of the document or webpage.
* **`FilenameBoost`** (double): Bonus applied if query keywords appear in the document's filename or URL string.
* **`TermInTextBoost`** (double): Bonus applied when individual query keywords match body text.
