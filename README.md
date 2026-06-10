# LocalSearchEngine

LocalSearchEngine is a fully self-hosted, local search platform built with C# and .NET. It allows you to build a local knowledge base by crawling web pages and documents, embedding them into a local vector database, and serving a search interface.

## Features

- **Web Crawler**: A built-in crawler (`LocalSearchEngine.Crawler`) that traverses links, respects domains, and deduplicates URLs.
- **Document Parsing**: 
  - Parses standard HTML pages.
  - Automatically extracts text from PDF documents.
  - Automatically extracts text from modern Microsoft Word (`.docx`) documents.
- **Smart Caching**: Respects `ETag` and `Last-Modified` HTTP headers to prevent re-crawling unmodified content.
- **Vector Search & Embeddings**: Uses `Microsoft.SemanticKernel` to generate local embeddings and stores them using `sqlite-vec` for high-performance similarity searches.
- **Local AI**: Fully local embeddings model (`LocalSearchEngine.ModelDownloader` component) ensures your indexed data never leaves your machine.

## Project Structure

- **`LocalSearchEngine.Core`**: The backbone of the application. Contains the `CrawlerService` (for fetching and parsing documents) and the `VectorSearchService` (for handling SQLite vector operations).
- **`LocalSearchEngine.Crawler`**: A console application/worker that initiates and manages the crawling process.
- **`LocalSearchEngine.ModelDownloader`**: Responsible for downloading and setting up the local embedding models required for Semantic Kernel.
- **`LocalSearchEngine.Web`**: The web-based frontend interface where users can interact with the vector search engine.

## Technologies Used

- **C# / .NET 10**
- **SQLite** & **sqlite-vec** (Vector extensions for SQLite)
- **HtmlAgilityPack** (HTML parsing)
- **iText** (PDF Text Extraction)
- **NPOI** (Microsoft Word `.docx` Text Extraction)
- **Microsoft Semantic Kernel**

## Getting Started

1. Ensure you have the .NET SDK installed.
2. Clone the repository and navigate to the project root.
3. Build the solution to restore NuGet packages:
   ```bash
   dotnet build
   ```
4. Run the Model Downloader to fetch the required local embedding models.
5. Start the Crawler to index your first seed URLs.
6. Launch the Web interface to search through your locally indexed documents!