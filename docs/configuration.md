# Configuration Guide

**LocalSearchEngine** is highly configurable through the `appsettings.json` files located in the `LocalSearchEngine.Crawler` and `LocalSearchEngine.Web` projects, as well as command-line arguments.

---

## Crawler Configuration (`LocalSearchEngine.Crawler/appsettings.json`)

The crawler's behaviors, bounds, and scope restrictions can be set in JSON format:

```json
{
  "db": "search.db",
  "max-pages": null,
  "max-pages-per-host": null,
  "max-crawl-size-bytes": 15728640,
  "allowed-servers": [
    "example.com",
    "https://wiki.example.org:8080"
  ],
  "check-external-links": false
}
```

### Settings Reference

* **`db`** (string, default: `"search.db"`): Path to output SQLite database file containing state and index data. Can be overridden using CLI argument `--db <path>`.
* **`max-pages`** (integer/null, default: `null`): A hard cap on how many successful pages the crawler will index this run. Stops the crawl once reached.
* **`max-pages-per-host`** (integer/null, default: `null`): Maximum pages to download from a single origin. Useful to prevent infinite crawl loops on calendar or search filter pages.
* **`max-crawl-size-bytes`** (integer, default: `15728640` / 15MB): The maximum size (after decompression) of an individual document or page. Large files are ignored or terminated mid-stream.
* **`allowed-servers`** (array of strings): Defines the scope of servers the crawler is permitted to visit. See **Crawl Scope Rules** below.
* **`check-external-links`** (boolean, default: `false`): If `true`, the crawler will verify off-site links found on crawled pages (by performing a lightweight `HEAD` or `GET` request) without indexing their content.

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

## Web Search Settings (`LocalSearchEngine.Web/appsettings.json`)

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
