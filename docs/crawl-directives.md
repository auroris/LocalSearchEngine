# Crawl Directives & Compliance

This page is the authoritative reference for the crawl directives **LocalSearchEngine** reads and obeys: `robots.txt`, HTTP headers, and HTML `<meta>`/element-level hints. Anything not listed here is not interpreted as a directive.

The crawler identifies itself with the user-agent token:

```
LocalSearchEngine-Bot/1.0
```

Two distinct decisions run throughout this page, and it helps to keep them separate:

* **Fetch / follow** — *may we request this URL, and do we follow the links on the page?*
* **Index** — *once fetched, do we store the page's content in the search index?*

A page can be fetched and followed but not indexed (that is exactly what "noindex, follow" means). The reverse never happens: we never index a page we did not fetch.

---

## `robots.txt`

The crawler fetches `/robots.txt` once per **origin** (scheme + host + port) on first contact, parses it with [`RobotsExclusionTools`](https://github.com/TurnerSoftware/RobotsExclusionTools), and caches the result for the rest of the run.

### User-agent groups

The rules applied are the group whose `User-agent` names our token `LocalSearchEngine-Bot/1.0`, or the `*` group when no group targets us specifically. To single this crawler out, a site can write:

```
User-agent: LocalSearchEngine-Bot
Disallow: /private/
```

### `Allow` / `Disallow`

* Both directives are honored, with `*` (wildcard) and `$` (end-of-path anchor) supported.
* **Longest-match precedence**: the most specific (longest) matching pattern wins; on an exact-length tie, `Allow` beats `Disallow`.
* A disallowed URL is **never fetched** (so it is never indexed, and its links are never discovered).
* A URL already in the index that a re-crawl finds newly disallowed is **removed** from the index in the post-crawl cleanup pass.

### `Crawl-delay`

* Respected as the minimum gap between requests to that host.
* Fractional values (e.g. `0.5`) are rounded **up** to whole seconds.
* Capped at **30 seconds** to keep a hostile or mistaken value from stalling the crawl.
* When no `Crawl-delay` is given, a default politeness delay of **250 ms** per host applies (this is configurable via the `request-delay-ms` setting in `appsettings.json` or the `--request-delay-ms` CLI option).

### `Sitemap`

* `Sitemap:` entries are read and used to **seed the frontier**, along with the origin's `/sitemap.xml`. Sitemap indexes are followed into their children.
* Only entries on the **seed's own origin** that `robots.txt` also allows are enqueued. (Sitemaps are parsed with DTD processing and external entity resolution disabled.)

### How `robots.txt` status codes are treated

| Response | Effect for that origin this run |
| --- | --- |
| `2xx` | Rules are parsed and applied (a body over the size cap is parsed truncated). |
| `5xx` | Treated as **disallow-all** for the run; the origin is also flagged *unavailable*, which **exempts its existing index entries from pruning**. |
| `4xx` (e.g. 403, 404) | Treated as **allow-all** (no restrictions). |
| Connection failure on first contact | Treated as allow-all, but the host is **written off as unreachable** and all of its URLs are skipped for the rest of the run (its existing index is preserved, not pruned). |

---

## HTTP response headers

### `X-Robots-Tag`

Parsed the same way as the robots `<meta>` tag, and may carry an optional user-agent prefix that is matched **case-insensitively** against our token:

```
X-Robots-Tag: noindex
X-Robots-Tag: LocalSearchEngine-Bot/1.0: noindex, nofollow
```

We **enforce** these directives:

| Directive | Effect |
| --- | --- |
| `noindex` | Page is fetched and its links followed, but its content is **not indexed**. |
| `nofollow` | Page may be indexed, but **none of its links are followed** or recorded. |
| `none` | Equivalent to `noindex, nofollow`. |

Other standard tokens (`index`, `follow`, `all`, `noarchive`, `nosnippet`, `max-snippet`, `max-image-preview`, `max-video-preview`, `notranslate`, `unavailable_after`) are **recognized** — so they are not mistaken for a user-agent name — but they are **not acted on**. (`index`, `follow`, and `all` are the defaults anyway.)

### `ETag` / `Last-Modified`

Stored when a page is indexed and replayed on the next crawl as `If-None-Match` / `If-Modified-Since` conditional requests, so an unchanged page returns a cheap `304 Not Modified` and is not re-downloaded or re-embedded.

### `Content-Type`

* The media type selects the extractor: `text/html` / `application/xhtml+xml` → HTML, anything containing `pdf` → PDF, `wordprocessingml` → DOCX. Generic types (`application/octet-stream`, `text/plain`, `application/zip`) are sniffed by magic bytes; unsupported types are skipped (fetched, not indexed).
* The `charset` parameter, when present, is authoritative for decoding the body.

### `Content-Length`

If the declared length exceeds the configured `max-crawl-size-bytes`, the body is skipped without downloading it in full. (The byte cap is also enforced while streaming, in case the header is missing or wrong.)

### Redirects (`Location`) and status codes

| Response | Effect |
| --- | --- |
| `3xx` redirect | Followed by the HTTP client; the **final URL** is re-checked for scope and `robots.txt`. An off-scope redirect is not followed — except the **seed** itself, which adopts the destination origin. A redirect to an already-seen URL is dropped. |
| `304 Not Modified` | Page is unchanged; its stored links are re-walked so the frontier keeps moving. |
| `404 Not Found` / `410 Gone` | Page is **removed** from the index. |
| Other non-2xx | Treated as a transient failure: the **existing index entry is kept** (temporary downtime is never destructive). |

---

## HTML `<meta>` tags and elements

### `<meta name="robots">`

Honored with the same enforced directives as `X-Robots-Tag` above (`noindex`, `nofollow`, `none`):

```html
<meta name="robots" content="noindex, follow">
```

### `<link rel="canonical">`

If a page declares a canonical URL that is in scope and different from the page's own URL, the page is treated as an **alias**: the canonical URL is enqueued and the aliased page's own content is not indexed.

### `rel="nofollow"`

An individual `<a rel="nofollow">` link is **not followed** (and not recorded as an off-site link). This is narrower than a page-level `nofollow`, which suppresses *all* links on the page.

### `<meta charset>` / `<meta http-equiv="Content-Type">`

Used to decode the page when the HTTP response did not specify a charset. (A meta declaration of UTF-16/UTF-32 is ignored as self-contradictory, per the HTML spec.)

### Content vs. boilerplate

Titles (`<title>`) and headings (`<h1>`–`<h6>`) are extracted and given ranking weight. For each followed in-scope link, the crawler also stores a compact description made from its anchor or accessible label, nearest paragraph-like block, nearest preceding heading, and source-page title. Search treats that inbound description as evidence about the target page.

Before text and links are harvested, chrome is stripped so it never reaches either the body index or link evidence: `script`, `style`, `nav`, `footer`, `header`, `svg`, `noscript`, `template`, and `aside`, plus form **controls** (`input`, `select`, `textarea`, `button`, `label`, `datalist`, `output`). Whole `<form>` elements are deliberately *not* removed, so pages that wrap their entire body in one form (Oracle APEX, ASP.NET WebForms) are still indexed.

---

## Request headers we send

| Header | Value / purpose |
| --- | --- |
| `User-Agent` | `LocalSearchEngine-Bot/1.0` |
| `If-None-Match` / `If-Modified-Since` | Conditional revalidation from the stored `ETag` / `Last-Modified` (see above). |
| `Accept-Encoding` | `gzip`, `deflate`, `br` — responses are transparently decompressed. |

---

## Operator-imposed directives

Beyond what a site declares about itself, you can impose your own rules from the crawler's `appsettings.json` — see the [Configuration Guide](configuration.md):

* **`allowed-servers`** bounds which hosts are in scope (a `Disallow`-like fetch boundary you control).
* **`noindex-patterns`** marks URL globs as **"noindex, follow"** from your side: matching pages are crawled for their links but never indexed, exactly as if the page had declared `noindex` itself.

> [!NOTE]
> When several sources speak, the page is left out of the index if **any** of them say `noindex` (a `robots`/`X-Robots-Tag` directive on the page, or a matching `noindex-patterns` entry). For following links, a page-level `nofollow` (from `<meta>` or `X-Robots-Tag`) stops link discovery; a `noindex-patterns` match never suppresses link following — that is the whole point of it.
