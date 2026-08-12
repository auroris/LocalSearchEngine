# LocalSearchEngine

LocalSearchEngine is a fully self-hosted, local search platform built with C# and .NET. 

A console crawler builds a knowledge base from web pages and documents into a single SQLite file (full-text index + vector embeddings), and a web app serves a hybrid search interface over it. Everything — crawling, embedding, and querying — runs entirely on your local machine; indexed data never leaves it.

---

## Key Features

* **Polite & Origin-Scoped Crawler**: Respects `robots.txt` rules, sitemaps, and `Crawl-delay` settings per origin. Includes crawler trap protection and site-cap controls.
* **Document Extraction**: Sniffs and extracts text from HTML pages (boilerplate stripped), PDFs, and Word (`.docx`) files.
* **Incremental Crawls**: Employs conditional HTTP requests, SHA-256 content hashing to avoid re-embedding duplicate text, and outlink persistence to crawl efficiently.
* **Feed-Driven Updates**: Point the crawler at your site's `rss.xml` with `--feed` and only the items the feed lists are fetched and re-indexed — nothing else is touched, so routine updates cost a handful of requests instead of a full crawl. Full crawls also auto-discover feeds advertised in page metadata and consult them as a positive freshness signal.
* **Automatic Incremental Crawls**: With `allow-incremental` enabled, a bare `crawler.exe` run seeds every configured site, walks each site's advertised feed newest-first, and — when the feed reaches back to an item already covered — crawls exactly the newer items and stops. When a feed can't prove the change list complete, the run falls back to a full crawl on its own.
* **Local AI Embeddings**: Uses `snowflake-arctic-embed-s` (384-dim, int8 ONNX) running locally on CPU via ONNX Runtime.
* **Hybrid Search Ranker**: Unions dense semantic candidates (`sqlite-vec`) with broad BM25 lexical candidates (SQLite FTS5), then applies reciprocal-rank fusion, phrase/proximity signals, term coverage, and structural field boosts.

---

## Documentation

Comprehensive conceptual and API documentation is built using **DocFX** and is available in the repository.

### Viewing the Docs Locally

To run the documentation portal locally:

1. Restore the DocFX CLI tool:
   ```bash
   dotnet tool restore
   ```
2. Build and serve the docs:
   ```bash
   dotnet docfx docfx.json --serve
   ```
3. Open `http://localhost:8080` in your web browser.

Conceptual guides are located in the `docs/` directory:
* [Introduction & Architecture](docs/introduction.md)
* [Getting Started Guide](docs/getting-started.md)
* [Configuration Guide](docs/configuration.md)

---

## Project Structure

* **`LocalSearchEngine.Core`**: Core logic for network fetching, parsing, vector embedding, and hybrid ranking.
* **`LocalSearchEngine.Crawler`**: Console application that crawls seed URLs and generates the SQLite search index.
* **`LocalSearchEngine.Web`**: ASP.NET Core web application serving the search UI and index statistics.
* **`LocalSearchEngine.Tests`**: Unit and integration test suite.

---

## Quick Start

1. **Build the Solution**:
   ```bash
   dotnet build
   ```
   *(This builds the binaries and copies the embedded model from the repository into your build outputs)*

2. **Crawl a Website**:
   ```bash
   dotnet run --project LocalSearchEngine.Crawler -- --db LocalSearchEngine.Web/search.db https://example.com
   ```

3. **Start the Search Web App**:
   ```bash
   dotnet run --project LocalSearchEngine.Web
   ```
   Navigate to `http://localhost:5000` to start searching.

For advanced settings, CLI options, and crawl scope rules, see the [Configuration Guide](docs/configuration.md).
