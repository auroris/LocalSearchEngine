# Getting Started

Follow this guide to get **LocalSearchEngine** built, configured, and running on your local machine.

---

## Prerequisites

* **.NET 10 SDK** or later.
* **SQLite** (usually pre-installed or bundled automatically via SQLite NuGet packages).

---

## 1. Setup and Build

First, clone the repository, restore NuGet packages, and build the solution:

```bash
# Clone the repository and navigate into it
git clone <repository-url>
cd LocalSearchEngine

# Restore tools and packages
dotnet restore
dotnet tool restore

# Build the solution
dotnet build
```

> [!NOTE]
> The embedding model (`snowflake-arctic-embed-s` ONNX model, ~32MB) is checked directly into the repository under the `LocalEmbeddingsModel` directory. During the build process, MSBuild targets copy this model and its vocabulary file into your build outputs automatically as configured in `Directory.Build.props`.

---

## 2. Running the Crawler

To populate the search index, run the console crawler against a target website seed URL:

```bash
dotnet run --project LocalSearchEngine.Crawler -- https://example.com
```

### Specifying the Database Path
By default, the crawler creates a database file named `search.db` in its running directory. To search this database in the Web app, it needs to be located in the `LocalSearchEngine.Web` folder. You can direct the crawler to save it there directly:

```bash
dotnet run --project LocalSearchEngine.Crawler -- --db LocalSearchEngine.Web/search.db https://example.com
```

### Common CLI Options

| Option | Default | Description |
| --- | --- | --- |
| `--db <path>` | `search.db` | Path to the SQLite database output. |
| `--max-pages <n>` | Unlimited | Stop crawling after downloading `n` successful pages. |
| `--max-pages-per-host <n>` | Unlimited | Maximum pages to crawl from any individual host (guards against calendar traps). |
| `--max-crawl-size-bytes <n>` | `15728640` (15 MB) | Exclude pages/files larger than `n` bytes. |

Example using page limits:
```bash
dotnet run --project LocalSearchEngine.Crawler -- --max-pages 100 --max-pages-per-host 20 https://example.com
```

---

## 3. Running the Search Web App

Once you have crawled some pages, start the ASP.NET Core web interface:

```bash
dotnet run --project LocalSearchEngine.Web
```

Once running, navigate to:
* **Search UI**: `http://localhost:5000` (or `https://localhost:5001`)
* **Stats API**: `http://localhost:5000/api/stats`

The web application opens the SQLite database in multi-user WAL mode, allowing you to search the index while a crawl is actively running and updating the database.

---

## 4. Serving Documentation Locally

To build and view this documentation website locally with API references:

```bash
# Build and serve DocFX
dotnet tool run docfx docfx.json --serve
```

Then open `http://localhost:8080` in your web browser.