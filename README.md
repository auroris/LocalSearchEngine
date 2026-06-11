# LocalSearchEngine

LocalSearchEngine is a fully self-hosted, local search platform built with C# and .NET. It allows you to build a local knowledge base by crawling web pages and documents, embedding them into a local vector database, and serving a search interface.

## Features

- **Web Crawler**: A built-in crawler (`LocalSearchEngine.Crawler`) that traverses links, respects domains, and deduplicates URLs. It is a polite citizen: it honors `robots.txt` (groups, `Allow`/`Disallow` with `*`/`$` wildcards, longest-match precedence, and `Crawl-delay`), spaces out requests, caps per-document download size, and can be stopped cleanly with `Ctrl+C`.
- **Document Parsing**: 
  - Parses standard HTML pages (decoding entities and preserving word boundaries between block elements).
  - Automatically extracts text from PDF documents.
  - Automatically extracts text from modern Microsoft Word (`.docx`) documents.
- **Smart Caching**: Respects `ETag` and `Last-Modified` HTTP headers to prevent re-crawling unmodified content.
- **Hybrid Search**: Combines semantic (vector) similarity with exact-phrase keyword matching (SQLite FTS5). Returns *every* result at or above a configurable similarity threshold (no fixed result-count cap), and boosts the ranking of exact-phrase, heading/title, and file-name matches.
- **Vector Search & Embeddings**: Uses `Microsoft.SemanticKernel` to generate local embeddings and stores them using `sqlite-vec` for high-performance similarity searches.
- **Local AI**: A fully local embedding model (`bge-small-en-v1.5`, 384-dim) runs on the CPU via ONNX Runtime, so your indexed data never leaves your machine. The model is fetched once at build time and bundled next to the binaries.

## Project Structure

- **`LocalSearchEngine.Core`**: The backbone of the application. Contains the `CrawlerService` (for fetching and parsing documents) and the `VectorSearchService` (for handling SQLite vector operations).
- **`LocalSearchEngine.Crawler`**: A console application/worker that initiates and manages the crawling process.
- **`LocalSearchEngine.Web`**: The web-based frontend interface where users can interact with the vector search engine.
- **`LocalSearchEngine.Tests`**: xUnit unit and integration tests.

## Technologies Used

- **C# / .NET 10**
- **SQLite** & **sqlite-vec** (Vector extensions for SQLite)
- **HtmlAgilityPack** (HTML parsing)
- **iText** (PDF Text Extraction)
- **NPOI** (Microsoft Word `.docx` Text Extraction)
- **Microsoft Semantic Kernel** & **SmartComponents.LocalEmbeddings** (`bge-small-en-v1.5` via ONNX Runtime)

> **Note**: Both `iText` and `NPOI` transitively depend on `System.Security.Cryptography.Xml` version `8.0.2`, which has known high-severity vulnerabilities (NU1903). To address this, we explicitly reference version `8.0.3` in `LocalSearchEngine.Core`.

## Getting Started

1. Ensure you have the .NET SDK installed.
2. Clone the repository and navigate to the project root.
3. Build the solution. This restores NuGet packages and, on the first build, downloads the local embedding model and bundles it next to the binaries:
   ```bash
   dotnet build
   ```
4. Start the Crawler to index your first seed URLs:
   ```bash
   dotnet run --project LocalSearchEngine.Crawler -- https://example.com
   ```
5. Launch the Web interface to search through your locally indexed documents!

### Database location

The search index lives alongside the web app's own files (its content root), so the
web app can serve it with no extra configuration. The crawler writes there by default
too — in the repo/dev layout it finds the sibling `LocalSearchEngine.Web` folder. Point
it somewhere else with the `--db <path>` flag, e.g.:

```bash
dotnet run --project LocalSearchEngine.Crawler -- --db LocalSearchEngine.Web/search.db https://example.com
```

On the web side, override the location with the `ConnectionStrings:SearchDb` configuration value.

## Testing

```bash
dotnet test
```

Unit tests cover the pure logic — URL normalization, the crawl extension filter,
robots.txt parsing/matching, text chunking, and search ranking (similarity threshold
plus exact-match/heading/file-name boosts). Integration tests run the real sqlite-vec
connector against a temporary database (with a deterministic fake embedder, so no model
download) to verify that a self-match ranks first and that deleting a URL clears its
data, vector, and FTS rows together. CI runs build + test on every push and pull
request (`.github/workflows/dotnet.yml`).