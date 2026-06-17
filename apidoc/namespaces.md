---
uid: LocalSearchEngine.Core
summary: *content
---

Root of the search-engine library and home of the cross-cutting configuration the subsystems share,
such as `DatabaseConfig` (the SQLite connection string). The crawling, searching, and text-processing
namespaces nested beneath it do the actual work.

---
uid: LocalSearchEngine.Core.Crawling
summary: *content
---

The crawl orchestrator. `CrawlerService` wires the engine, policies, storage, and reporting together
and runs a crawl as a bounded producer/consumer — one task fetching and classifying pages, another
persisting them — returning a `CrawlReport` describing everything the run indexed, removed, and
discovered.

---
uid: LocalSearchEngine.Core.Crawling.Engine
summary: *content
---

The moving parts of a single crawl: the producer that fetches and classifies each URL, the consumer
that writes the results, and the workers they lean on — page downloader, robots.txt and sitemap
services, and the end-of-crawl link verifier — plus the shared per-crawl `CrawlContext` and the
`CrawlJob` records that pass between producer and consumer.

---
uid: LocalSearchEngine.Core.Crawling.Extraction
summary: *content
---

Turns a fetched response body into indexable content. `ContentExtractor` pulls the title, headings,
visible text, robots directives, canonical link, and outlinks out of HTML, and the title and text out
of PDF and DOCX files, stripping navigation and boilerplate so they never reach the index.

---
uid: LocalSearchEngine.Core.Crawling.Policies
summary: *content
---

The crawl's decision-making, kept free of network I/O so it stays easy to reason about and test:
which hosts are in scope (`AllowedHosts`), what robots.txt permits (`RobotsRules`, `CrawlPolicy`),
how URLs are normalized and keyed by origin (`UrlNormalizer`, `UrlOrigin`), how content types are
classified, and which hosts have been written off as unreachable (`HostHealthTracker`).

---
uid: LocalSearchEngine.Core.Crawling.Reporting
summary: *content
---

How a crawl reports on itself. `ICrawlObserver`/`CrawlObserver` centralize logging and statistics;
`ICrawlReporter` is the contract a host (CLI, web, tests) implements to present progress; and the
surrounding value types — outcomes, phases, the running `CrawlStats` and its immutable snapshots, and
the end-of-run `CrawlReport` — carry that information.

---
uid: LocalSearchEngine.Core.Crawling.Storage
summary: *content
---

The crawl's SQLite persistence layer. `CrawlStore` owns the schema — crawl state, the link index, and
the full-text-search mirror with its triggers and indexes — and every read and write against it.

---
uid: LocalSearchEngine.Core.Searching
summary: *content
---

The query side of the index. `VectorSearchService` runs a hybrid search — semantic vector similarity
plus FTS5 keyword matching — over the stored chunks; `SearchQueryParser` peels off `site:` filters,
`SearchRanker` blends and boosts the candidates per `SearchSettings`, and the result and response
records carry the ranked output.

---
uid: LocalSearchEngine.Core.TextProcessing
summary: *content
---

Prepares text for the index. `TextChunker` splits documents into overlapping, semantically-aware
chunks, and the `IEmbedder` abstraction (with its local CPU/ONNX adapter) turns each chunk and each
query into a vector embedding.

---
uid: LocalSearchEngine.Crawler
summary: *content
---

The crawler's console front-end: the live (Spectre.Console) and plain progress reporters, the
end-of-run summary panel, and the writers that persist the crawl report as JSON, a text summary, and
a broken-links report. The program entry point parses options and drives these.

---
uid: LocalSearchEngine.Tests
summary: *content
---

The automated test suite (xUnit), covering the crawl policies, text chunking, query parsing and
ranking, content extraction, and end-to-end crawl and vector-search integration.

---
uid: LocalSearchEngine.Web.Controllers
summary: *content
---

The web app's ASP.NET Core API controllers: `SearchController` runs hybrid queries and
`StatsController` reports index size and freshness, each returning a clear message to build the index
first when the crawler hasn't run yet.
