# LocalSearchEngine

LocalSearchEngine is a fully self-hosted, local search platform built with C# and .NET. A console crawler builds a knowledge base from web pages and documents into a single SQLite file (full-text index + vector embeddings), and a web app serves a hybrid search interface over it. Everything — crawling, embedding, and querying — runs on your machine; indexed data never leaves it.

## Features

### Crawler (`LocalSearchEngine.Crawler`)

- **Polite by design**: honors `robots.txt` (user-agent groups, `Allow`/`Disallow` with `*`/`$` wildcards, longest-match precedence) via [RobotsExclusionTools](https://github.com/TurnerSoftware/RobotsExclusionTools). `Crawl-delay` is respected — fractional values are rounded up to whole seconds, and the delay is capped at 30s so a misconfigured file can't stall the crawl. A 5xx from `robots.txt` is treated as *disallow-all* (per RFC 9309); a 4xx as *no restrictions*. Requests to the same host are spaced at least 250 ms apart.
- **Origin-scoped, lazy robots**: rules and sitemaps are tracked per origin (scheme + host + port), so a site on a non-default port gets its `robots.txt` from that port. Robots are fetched lazily on first contact — a host that's merely *allowed* is never contacted.
- **Sitemap discovery** for the seed's origin only: `Sitemap:` directives in robots.txt plus the conventional `/sitemap.xml`, including nested sitemap indexes (XXE-safe parsing). Only entries on the seed's own origin are taken — sitemaps never bulk-enumerate other hosts, even allowed ones.
- **Incremental re-crawls**: conditional requests with `ETag`/`Last-Modified`, SHA-256 content hashing to skip re-embedding unchanged bodies, and stored per-page outlinks so a 304/unchanged page still keeps the frontier growing.
- **Deduplication and aliasing**: byte-identical content served under two URLs is indexed once; `rel="canonical"` links are followed instead of indexed; redirects clean up the index entry of their source. A seed that redirects to a different host adopts that host into scope.
- **Robots directives on pages**: `noindex`/`nofollow`/`none` from `<meta name="robots">`, bot-specific meta tags, and the `X-Robots-Tag` header are honored; 404/410 pages are removed from the index; transient errors (5xx) never erase previously indexed content.
- **Graceful shutdown**: `Ctrl+C` stops fetching and flushes in-flight indexing work before exiting.
- **Trap protection**: an optional per-host page cap (`--max-pages-per-host`) guards against crawler traps like calendars and faceted navigation.

### Document handling

- Content is classified by the server's `Content-Type`, falling back to magic-byte sniffing — never by the URL's file extension. `/page.php` and `/release-1.0` crawl just fine; JSON or images served at pretty URLs are skipped.
- HTML: titles, headings, and visible text are extracted with boilerplate (nav, header, footer, scripts, form controls, etc.) stripped. Pages whose entire body sits inside a `<form>` (Oracle APEX, ASP.NET WebForms) are indexed normally — only the form *controls* are treated as chrome.
- PDF text extraction (iText) and modern Word `.docx` extraction (NPOI), with embedded document titles indexed like HTML titles.

### Search (`LocalSearchEngine.Web`)

- **Hybrid ranking**: semantic (vector cosine similarity) + keyword (SQLite FTS5 with porter stemming) in two tiers — verbatim phrase and all-terms.
- Returns *every* result at or above a configurable similarity threshold (no fixed result-count cap), with score boosts for exact-phrase, all-terms, heading, title, file-name, and literal-text matches. All weights are tunable under `SearchSettings` in `appsettings.json`.
- **Local AI**: the embedding model (`snowflake-arctic-embed-s`, 384-dim, int8 ONNX, ~32 MB) runs on the CPU via ONNX Runtime. It is downloaded once at build time and bundled next to the binaries — see `Directory.Build.props`.

## Crawl scope

The seed URL's exact origin — scheme, host, and port (the scheme's default port if none is given) — is always in scope. Additional hosts come from the `allowed-servers` array in the crawler's `appsettings.json`, with entries of the form:

```
[scheme://]host[:port]
```

- `example.com` — any scheme, any port
- `https://example.com` — HTTPS only, any port
- `example.com:8080` — any scheme, port 8080 only
- `http://example.com:8080` — exactly that origin

An omitted scheme or port matches anything. **The `www.` variant of a host is not implied** — if a site lives on both `example.com` and `www.example.com`, list both.

Allowed hosts are a filter, not a target list. Being allowed means the crawler *may* fetch a page on that host when links lead there — it is not a commitment to index the server wholesale: a host that nothing links to is never contacted (not even for robots.txt), and sitemap enumeration applies only to the seed. To fully index another server, run the crawler again with that server as the seed.

## Project Structure

- **`LocalSearchEngine.Core`**: the backbone — crawling (`CrawlerService`, content extraction, robots/URL policy), text chunking, and hybrid search (`VectorSearchService`, `SearchRanker`).
- **`LocalSearchEngine.Crawler`**: console app that runs the crawl and builds the index.
- **`LocalSearchEngine.Web`**: ASP.NET Core app serving the search API (`/api/search/query`) and a static frontend.
- **`LocalSearchEngine.Tests`**: xUnit unit and integration tests.

## Technologies Used

- **C# / .NET 10**
- **SQLite** with **FTS5** and **sqlite-vec** (via `Microsoft.SemanticKernel.Connectors.SqliteVec`)
- **HtmlAgilityPack** (HTML parsing), **iText** (PDF), **NPOI** (`.docx`)
- **RobotsExclusionTools** (robots.txt parsing)
- **SmartComponents.LocalEmbeddings** (`snowflake-arctic-embed-s` via ONNX Runtime)

> **Note**: Both `iText` and `NPOI` transitively depend on `System.Security.Cryptography.Xml` version `8.0.2`, which has known high-severity vulnerabilities (NU1903). To address this, we explicitly reference version `8.0.3` in `LocalSearchEngine.Core`.

## Getting Started

1. Ensure you have the .NET SDK installed.
2. Clone the repository and navigate to the project root.
3. Build the solution. This restores NuGet packages and, on the first build, downloads the local embedding model and bundles it next to the binaries:
   ```bash
   dotnet build
   ```
4. Run the crawler against a seed URL:
   ```bash
   dotnet run --project LocalSearchEngine.Crawler -- https://example.com
   ```
5. Launch the web interface and search your index:
   ```bash
   dotnet run --project LocalSearchEngine.Web
   ```

### Crawler options

| Option | Default | Description |
|---|---|---|
| `--db <path>` | `search.db` | Path to the SQLite database. |
| `--max-pages <n>` | unlimited | Pages to index this run (304s, skips, and failures don't count). |
| `--max-pages-per-host <n>` | unlimited | Stop indexing a host after it contributes n pages. |

Each option can also be set in the crawler's `appsettings.json` (`db`, `max-pages`, `max-pages-per-host`), which is also where `allowed-servers` lives.

### Database location

The crawler writes `search.db` relative to the directory it is run from; the web app looks for it in its own content root (`LocalSearchEngine.Web/`). To keep the index where the web app serves it, point the crawler there explicitly:

```bash
dotnet run --project LocalSearchEngine.Crawler -- --db LocalSearchEngine.Web/search.db https://example.com
```

On the web side, the `db` setting names the file (relative to the content root), and a full `ConnectionStrings:SearchDb` connection string overrides it entirely. The web app opens the database read-write because a WAL reader needs write access to the shared-memory index, so searching works even while a crawl is running.

## Testing

```bash
dotnet test
```

Unit tests cover the pure logic — URL normalization, allowed-host rules, robots.txt parsing/matching (including fractional crawl delays), content classification, FTS5 match semantics, text chunking, and search ranking. Integration tests drive the real crawl loop against a fake HTTP server and the real sqlite-vec connector in a temporary database (with a deterministic fake embedder, so no model download), covering 304 handling, redirects, deduplication, per-host caps, form-wrapped pages, non-default ports, and crawl-scope rules.
