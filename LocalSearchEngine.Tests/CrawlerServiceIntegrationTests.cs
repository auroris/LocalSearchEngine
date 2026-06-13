using LocalSearchEngine.Core;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Extraction;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Storage;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.TextProcessing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Drives the crawl loop end to end against a fake HTTP server and a real sqlite-vec
/// database, verifying the behaviours unit tests can't reach: that transient failures
/// don't erase the index, that "gone" pages are removed, that 304/unchanged pages still
/// keep the frontier growing, and that non-HTML and out-of-scope redirects aren't indexed.
/// </summary>
public sealed class CrawlerServiceIntegrationTests : IDisposable
{
    private const string Seed = "http://test.local/";
    private const string Page2 = "http://test.local/page2";

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ServiceProvider _provider;
    private readonly VectorSearchService _search;
    private readonly FakeEmbedder _embedder = new();
    private readonly FakeHandler _handler = new();
    private readonly HttpClient _httpClient;

    public CrawlerServiceIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lse_crawl_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";

        var services = new ServiceCollection();
        services.AddSqliteVectorStore(_ => _connectionString);
        _provider = services.BuildServiceProvider();

        var store = _provider.GetRequiredService<VectorStore>();
        var settings = Options.Create(new SearchSettings { MaxDistance = 1.0, CandidatePoolSize = 100 });
        _search = new VectorSearchService(_embedder, store, new DatabaseConfig(_connectionString), settings, NullLogger<VectorSearchService>.Instance);
        _httpClient = new HttpClient(_handler);
    }

    private CrawlerService NewCrawler() =>
        new(_httpClient, _search, NullLogger<CrawlerService>.Instance, new DatabaseConfig(_connectionString));

    private async Task EnsureSchemaAsync()
    {
        await _search.EnsureCreatedAsync();
        await NewCrawler().EnsureCreatedAsync();
    }

    [Fact]
    public async Task Transient_5xx_on_recrawl_keeps_existing_index()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>welcome home</p>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);
        Assert.True(ChunkCount(Seed) > 0);
        long before = ChunkCount(Seed);

        // The page now fails with a server error; its previously indexed content must survive.
        _handler.Routes[Seed] = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.Equal(before, ChunkCount(Seed));
    }

    [Fact]
    public async Task NotFound_removes_page_from_index()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/page2\">two</a>");
        _handler.Routes[Page2] = _ => Html("<title>Two</title><p>second page</p>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);
        Assert.True(ChunkCount(Page2) > 0);

        // page2 is gone now.
        _handler.Routes[Page2] = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.Equal(0, ChunkCount(Page2));
        Assert.True(ChunkCount(Seed) > 0); // the still-good page stays indexed

        // Verify that the metadata columns for the deleted page (Page2) are set to NULL in the database
        using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Title, ETag, LastModified, ContentHash FROM CrawlState WHERE Url = @Url";
            command.Parameters.AddWithValue("@Url", Page2);
            using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0), "Title should be NULL");
            Assert.True(reader.IsDBNull(1), "ETag should be NULL");
            Assert.True(reader.IsDBNull(2), "LastModified should be NULL");
            Assert.True(reader.IsDBNull(3), "ContentHash should be NULL");
        }
    }

    [Fact]
    public async Task NotModified_304_still_reaches_pages_linked_only_from_it()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/page2\">two</a>", etag: "\"v1\"");
        _handler.Routes[Page2] = _ => Html("<title>Two</title><p>alpha</p>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        // On re-crawl the seed is unchanged (304). page2 is only discoverable through the seed,
        // so unless the 304 path re-enqueues the seed's stored outlinks, it is never re-fetched.
        _handler.Routes[Seed] = req => req.Headers.IfNoneMatch.Any()
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Html("<title>Home</title><p>home</p> <a href=\"/page2\">two</a>", etag: "\"v1\"");

        _handler.Requested.Clear();
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.Contains(Page2, _handler.Requested);
    }

    [Fact]
    public async Task Unchanged_content_skips_reembedding()
    {
        await EnsureSchemaAsync();

        // No validators, so the server always returns 200 with a full body; the content hash
        // is what spares us from re-embedding identical bytes.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>stable content here</p>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);
        int embedsAfterFirst = _embedder.EmbedCount;
        Assert.True(embedsAfterFirst > 0);

        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.Equal(embedsAfterFirst, _embedder.EmbedCount); // no re-embedding of identical content
    }

    [Fact]
    public async Task Non_html_content_is_not_indexed()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/api\">data</a>");
        _handler.Routes["http://test.local/api"] = _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"k\":1}", Encoding.UTF8, "application/json") };

        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.Contains("http://test.local/api", _handler.Requested); // we did fetch it
        Assert.Equal(0, ChunkCount("http://test.local/api"));         // but did not index JSON
    }

    [Fact]
    public async Task Crawls_a_link_graph_and_indexes_every_reachable_page()
    {
        await EnsureSchemaAsync();

        // seed -> p2, p3 ; p2 -> p4. Every page must make it through the fetch/index pipeline.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><a href=\"/p2\">2</a> <a href=\"/p3\">3</a>");
        _handler.Routes["http://test.local/p2"] = _ => Html("<title>P2</title><a href=\"/p4\">4</a>");
        _handler.Routes["http://test.local/p3"] = _ => Html("<title>P3</title><p>three</p>");
        _handler.Routes["http://test.local/p4"] = _ => Html("<title>P4</title><p>four</p>");

        await NewCrawler().CrawlAsync(Seed, maxPages: 50);

        Assert.True(ChunkCount(Seed) > 0);
        Assert.True(ChunkCount("http://test.local/p2") > 0);
        Assert.True(ChunkCount("http://test.local/p3") > 0);
        Assert.True(ChunkCount("http://test.local/p4") > 0);
    }

    [Fact]
    public async Task Redirect_to_external_host_is_not_indexed()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/out\">leave</a>");
        // Simulate the request having been redirected off-site: the response's final request URI
        // is on a host outside the allowed set.
        _handler.Routes["http://test.local/out"] = _ =>
        {
            var resp = Html("<title>Evil</title><p>tracking beacon</p>");
            resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://external.example/landing");
            return resp;
        };

        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.Equal(0, ChunkCount("http://external.example/landing"));
        Assert.Equal(0, ChunkCount("http://test.local/out"));
        Assert.True(ChunkCount(Seed) > 0);
    }

    [Fact]
    public async Task Page_body_wrapped_in_a_form_is_still_indexed()
    {
        await EnsureSchemaAsync();

        // Platforms like Oracle APEX and ASP.NET WebForms wrap the entire page body in one
        // <form>; only the controls inside it are chrome, not the content.
        _handler.Routes[Seed] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<html><head><title>Form</title></head><body><form id=\"wwvFlowForm\">" +
                "<h1>Report heading</h1><p>narrative content inside the form</p>" +
                "<label>ZZLABELZZ</label><input type=\"text\" value=\"x\"><button>ZZBUTTONZZ</button>" +
                "</form></body></html>",
                Encoding.UTF8, "text/html")
        };

        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.True(HasChunkContaining(Seed, "narrative content inside the form"));
        Assert.False(HasChunkContaining(Seed, "ZZLABELZZ"));  // form controls stay chrome
        Assert.False(HasChunkContaining(Seed, "ZZBUTTONZZ"));
    }

    [Fact]
    public async Task Dotted_and_dynamic_paths_are_crawled_and_classified_by_content_type()
    {
        await EnsureSchemaAsync();

        // URLs are never filtered by how their path looks (".0" or ".php" are not file
        // extensions to us); the fetched Content-Type decides what gets indexed.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/release-1.0\">notes</a> <a href=\"/page.php\">php</a>");
        _handler.Routes["http://test.local/release-1.0"] = _ => Html("<title>Release</title><p>release notes</p>");
        _handler.Routes["http://test.local/page.php"] = _ => Html("<title>PHP</title><p>dynamic page</p>");

        await NewCrawler().CrawlAsync(Seed, maxPages: 50);

        Assert.True(ChunkCount("http://test.local/release-1.0") > 0);
        Assert.True(ChunkCount("http://test.local/page.php") > 0);
    }

    [Fact]
    public async Task Html_mislabeled_as_octet_stream_is_sniffed_and_indexed()
    {
        await EnsureSchemaAsync();

        // The server returns real HTML but labels it application/octet-stream. We should sniff the
        // bytes (leading <!DOCTYPE html) instead of trusting the generic type or the URL extension.
        _handler.Routes[Seed] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<!DOCTYPE html><html><head><title>Sniffed</title></head><body><p>real html body here</p></body></html>",
                Encoding.UTF8, "application/octet-stream")
        };

        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.True(ChunkCount(Seed) > 0);
    }

    [Fact]
    public async Task Www_variant_of_seed_host_is_out_of_scope_unless_allowed()
    {
        await EnsureSchemaAsync();

        // The www variant is a different host and is NOT implied by the apex seed.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"http://www.test.local/page\">www</a>");
        _handler.Routes["http://www.test.local/page"] = _ => Html("<title>WWW</title><p>on the www host</p>");

        await NewCrawler().CrawlAsync(Seed, maxPages: 50);

        Assert.Equal(0, ChunkCount("http://www.test.local/page"));
        Assert.DoesNotContain(_handler.Requested, u => u.Contains("www.test.local"));
    }

    [Fact]
    public async Task Allowed_servers_bring_additional_hosts_into_scope()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"http://www.test.local/page\">www</a>");
        _handler.Routes["http://www.test.local/page"] = _ => Html("<title>WWW</title><p>on the www host</p>");

        await NewCrawler().CrawlAsync(Seed, maxPages: 50, allowedServers: new[] { "www.test.local" });

        Assert.True(ChunkCount("http://www.test.local/page") > 0);
    }

    [Fact]
    public async Task Allowed_hosts_are_not_contacted_unless_the_crawl_reaches_them()
    {
        await EnsureSchemaAsync();

        // Listing a host as allowed is a filter, not a request to go probe it: no robots.txt,
        // sitemap, or page fetch may hit an allowed host that nothing links to.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home, no outlinks</p>");

        await NewCrawler().CrawlAsync(Seed, maxPages: 5, allowedServers: new[] { "other.local" });

        Assert.DoesNotContain(_handler.Requested, u => u.Contains("other.local"));
    }

    [Fact]
    public async Task Sitemap_entries_are_limited_to_the_seed_origin()
    {
        await EnsureSchemaAsync();

        // The seed's sitemap lists one of its own pages plus a page on another *allowed*
        // host. Sitemaps only bulk-enumerate the seed's origin: allowed hosts may be fetched
        // when links lead there, but their sitemap entries must be ignored entirely.
        _handler.Routes["http://test.local/sitemap.xml"] = _ => Xml(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
            "<url><loc>http://test.local/from-sitemap</loc></url>" +
            "<url><loc>http://other.local/from-sitemap</loc></url>" +
            "</urlset>");
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p>");
        _handler.Routes["http://test.local/from-sitemap"] = _ => Html("<title>Mine</title><p>seed sitemap page</p>");
        _handler.Routes["http://other.local/from-sitemap"] = _ => Html("<title>Other</title><p>other host page</p>");

        await NewCrawler().CrawlAsync(Seed, maxPages: 50, allowedServers: new[] { "other.local" });

        Assert.True(ChunkCount("http://test.local/from-sitemap") > 0);
        Assert.Equal(0, ChunkCount("http://other.local/from-sitemap"));
        Assert.DoesNotContain("http://other.local/from-sitemap", _handler.Requested);
    }

    [Fact]
    public async Task Robots_and_sitemap_requests_use_the_seeds_port()
    {
        await EnsureSchemaAsync();

        // A seed on a non-default port must have robots.txt and the sitemap probed on that
        // port, and pages on the default port stay out of scope.
        const string portSeed = "http://test.local:8080/";
        _handler.Routes[portSeed] = _ => Html("<title>Home</title><p>home on a port</p>");

        await NewCrawler().CrawlAsync(portSeed, maxPages: 5);

        Assert.Contains("http://test.local:8080/robots.txt", _handler.Requested);
        Assert.Contains("http://test.local:8080/sitemap.xml", _handler.Requested);
        Assert.DoesNotContain("http://test.local/robots.txt", _handler.Requested);
        Assert.True(ChunkCount(portSeed) > 0);
    }

    [Fact]
    public async Task Seed_redirect_to_a_new_host_adopts_that_host()
    {
        await EnsureSchemaAsync();

        // The seed redirects to an unrelated host (e.g. a vanity domain -> the real site). The
        // final host isn't in scope initially, but because the *seed* is what redirected, the
        // crawler should adopt the destination host and keep crawling there.
        _handler.Routes[Seed] = _ =>
        {
            var resp = Html("<title>Real</title><p>the real site</p> <a href=\"http://real.example/about\">about</a>");
            resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://real.example/");
            return resp;
        };
        _handler.Routes["http://real.example/about"] = _ => Html("<title>About</title><p>about page</p>");

        await NewCrawler().CrawlAsync(Seed, maxPages: 50);

        Assert.True(ChunkCount("http://real.example/") > 0);       // redirected content indexed under the new host
        Assert.True(ChunkCount("http://real.example/about") > 0);  // and its in-scope links followed
    }

    [Fact]
    public async Task Identical_content_on_two_urls_is_indexed_once()
    {
        await EnsureSchemaAsync();

        // Two pages whose bytes are identical (e.g. the same article under two paths). Only the
        // first crawled should be indexed; the second is aliased to it with no chunks of its own.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/a\">a</a> <a href=\"/b\">b</a>");
        const string duplicate = "<title>Same</title><p>one identical body of text</p>";
        _handler.Routes["http://test.local/a"] = _ => Html(duplicate);
        _handler.Routes["http://test.local/b"] = _ => Html(duplicate);

        await NewCrawler().CrawlAsync(Seed, maxPages: 50);

        long a = ChunkCount("http://test.local/a");
        long b = ChunkCount("http://test.local/b");
        Assert.True((a > 0) ^ (b > 0), $"expected exactly one of a/b to be indexed, got a={a}, b={b}");
    }

    [Fact]
    public async Task Per_host_cap_stops_indexing_after_n_pages()
    {
        await EnsureSchemaAsync();

        // seed -> p2, p3 ; p2 -> p4. With a cap of 2, only the seed and the next page index.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home page</p> <a href=\"/p2\">2</a> <a href=\"/p3\">3</a>");
        _handler.Routes["http://test.local/p2"] = _ => Html("<title>P2</title><p>two</p> <a href=\"/p4\">4</a>");
        _handler.Routes["http://test.local/p3"] = _ => Html("<title>P3</title><p>three</p>");
        _handler.Routes["http://test.local/p4"] = _ => Html("<title>P4</title><p>four</p>");

        await NewCrawler().CrawlAsync(Seed, maxPages: 50, maxPagesPerHost: 2);

        Assert.Equal(2, CountIndexedUrls());
    }

    [Fact]
    public async Task Redirect_cleans_up_old_index_and_metadata()
    {
        await EnsureSchemaAsync();

        // 1. Initial crawl: index http://test.local/page1
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/page1\">page1</a>");
        _handler.Routes["http://test.local/page1"] = _ => Html("<title>Page 1</title><p>This is the first page</p> <a href=\"/page1-out\">outlink</a>");
        _handler.Routes["http://test.local/page1-out"] = _ => Html("<title>Outlink</title><p>outlink content</p>");

        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        // Verify page1 is indexed
        Assert.True(ChunkCount("http://test.local/page1") > 0);
        Assert.True(HasOutlinks("http://test.local/page1"));

        // 2. Second crawl: page1 now redirects to page2
        _handler.Routes["http://test.local/page1"] = _ =>
        {
            var resp = Html("<title>Page 2</title><p>This is the second page</p>");
            resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://test.local/page2");
            return resp;
        };
        _handler.Routes["http://test.local/page2"] = _ => Html("<title>Page 2</title><p>This is the second page</p>");

        // Run crawl again
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        // Verify page2 is indexed, page1 is cleaned up from the index
        Assert.True(ChunkCount("http://test.local/page2") > 0);
        Assert.Equal(0, ChunkCount("http://test.local/page1"));

        // Verify page1 outlinks are deleted
        Assert.False(HasOutlinks("http://test.local/page1"));

        // Verify page1 crawl state has status code 302 and cleared metadata (Title is null)
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT StatusCode, Title, ContentHash FROM CrawlState WHERE Url = 'http://test.local/page1'";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(302, reader.GetInt32(0));
        Assert.True(reader.IsDBNull(1)); // Title should be null
        Assert.True(reader.IsDBNull(2)); // ContentHash should be null
    }

    [Fact]
    public async Task Stale_pages_are_pruned_after_a_completed_crawl()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/page2\">two</a>");
        _handler.Routes[Page2] = _ => Html("<title>Two</title><p>second page</p>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);
        Assert.True(ChunkCount(Page2) > 0);

        // The site stops linking to page2 (it isn't 404 — just orphaned). A completed re-crawl
        // can no longer reach it, so it must drop out of the index and the crawl-state table.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home without the link</p>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.Equal(0, ChunkCount(Page2));
        Assert.False(HasCrawlState(Page2));
        Assert.True(ChunkCount(Seed) > 0); // the reachable page is untouched
    }

    [Fact]
    public async Task Capped_crawl_does_not_prune()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home v1</p> <a href=\"/page2\">two</a>");
        _handler.Routes[Page2] = _ => Html("<title>Two</title><p>second page</p>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);
        Assert.True(ChunkCount(Page2) > 0);

        // Changed seed content + maxPages 1: the seed re-indexes, hits the cap, and page2 is
        // left sitting in the queue. "Not visited" means nothing here, so nothing may be pruned.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home v2</p> <a href=\"/page2\">two</a>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 1);

        Assert.True(ChunkCount(Page2) > 0);
        Assert.True(HasCrawlState(Page2));
    }

    [Fact]
    public async Task Pruning_leaves_other_hosts_rows_alone()
    {
        await EnsureSchemaAsync();

        // Two sites share the database, each crawled with its own seed (the documented
        // multi-site workflow).
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/page2\">two</a>");
        _handler.Routes[Page2] = _ => Html("<title>Two</title><p>second page</p>");
        _handler.Routes["http://other.local/"] = _ => Html("<title>Other</title><p>another site entirely</p>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);
        await NewCrawler().CrawlAsync("http://other.local/", maxPages: 5);
        Assert.True(ChunkCount("http://other.local/") > 0);
        Assert.True(ChunkCount(Page2) > 0); // crawling site B never prunes site A either

        // Re-crawl site A with page2 orphaned: page2 is pruned, site B is out of this
        // crawl's scope and must survive untouched.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home without the link</p>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.Equal(0, ChunkCount(Page2));
        Assert.True(ChunkCount("http://other.local/") > 0);
        Assert.True(HasCrawlState("http://other.local/"));
    }

    [Fact]
    public async Task Robots_5xx_for_an_origin_protects_it_from_pruning()
    {
        await EnsureSchemaAsync();

        const string wwwPage = "http://www.test.local/page";
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"http://www.test.local/page\">www</a>");
        _handler.Routes[wwwPage] = _ => Html("<title>WWW</title><p>on the www host</p>");
        await NewCrawler().CrawlAsync(Seed, maxPages: 5, allowedServers: new[] { "www.test.local" });
        Assert.True(ChunkCount(wwwPage) > 0);

        // On the re-crawl the www host's robots.txt is down (5xx = disallow-all for now), so its
        // page goes unvisited — but unavailable robots says nothing about the page being gone,
        // and pruning must leave that origin alone.
        _handler.Routes["http://www.test.local/robots.txt"] = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await NewCrawler().CrawlAsync(Seed, maxPages: 5, allowedServers: new[] { "www.test.local" });

        Assert.True(ChunkCount(wwwPage) > 0);
        Assert.True(HasCrawlState(wwwPage));
    }

    [Fact]
    public async Task Oversized_sitemap_is_skipped_but_crawl_proceeds()
    {
        await EnsureSchemaAsync();

        // The sitemap blows the crawl-wide size limit; it must be abandoned (so its entries are
        // never enqueued) without harming the rest of the crawl.
        var bloated = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
            "<url><loc>http://test.local/from-sitemap</loc></url>" +
            $"<!-- {new string('x', 4000)} -->" +
            "</urlset>";
        _handler.Routes["http://test.local/sitemap.xml"] = _ => Xml(bloated);
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>small home page</p>");
        _handler.Routes["http://test.local/from-sitemap"] = _ => Html("<title>Listed</title><p>sitemap-only page</p>");

        await NewCrawler().CrawlAsync(Seed, maxPages: 5, maxCrawlSizeBytes: 1024);

        Assert.True(ChunkCount(Seed) > 0);
        Assert.Equal(0, ChunkCount("http://test.local/from-sitemap"));
        Assert.DoesNotContain("http://test.local/from-sitemap", _handler.Requested);
    }

    private bool HasCrawlState(string url)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM CrawlState WHERE Url = @u";
        command.Parameters.AddWithValue("@u", url);
        return (long)(command.ExecuteScalar() ?? 0L) > 0;
    }

    private bool HasOutlinks(string url)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM CrawlLinks WHERE FromUrl = @u";
        command.Parameters.AddWithValue("@u", url);
        return (long)(command.ExecuteScalar() ?? 0L) > 0;
    }

    private long ChunkCount(string url)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM text_chunks WHERE Url = @u";
        command.Parameters.AddWithValue("@u", url);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private bool HasChunkContaining(string url, string fragment)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM text_chunks WHERE Url = @u AND Text LIKE '%' || @f || '%'";
        command.Parameters.AddWithValue("@u", url);
        command.Parameters.AddWithValue("@f", fragment);
        return (long)(command.ExecuteScalar() ?? 0L) > 0;
    }

    private long CountIndexedUrls()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(DISTINCT Url) FROM text_chunks";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    [Fact]
    public async Task Crawl_skips_files_exceeding_max_crawl_size_bytes()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<a href=\"/largefile\">large</a>");
        _handler.Routes["http://test.local/largefile"] = _ => Html(new string('x', 300));

        // Seed page is ~97 bytes (should succeed), largefile is >300 bytes (should be skipped under 150 byte limit)
        await NewCrawler().CrawlAsync(Seed, maxPages: 5, maxCrawlSizeBytes: 150);

        Assert.True(ChunkCount(Seed) > 0);
        Assert.Equal(0, ChunkCount("http://test.local/largefile")); // largefile is skipped
    }

    [Fact]
    public async Task Crawl_skips_unsupported_content_types_by_header()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><a href=\"/image.png\">image</a>");
        _handler.Routes["http://test.local/image.png"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0, 1, 2, 3 })
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png") }
            }
        };

        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.True(ChunkCount(Seed) > 0);
        Assert.Equal(0, ChunkCount("http://test.local/image.png"));
    }

    [Fact]
    public async Task Crawl_aborts_downloading_large_files_when_magic_bytes_do_not_match()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><a href=\"/badfile\">bad file</a>");
        
        // Return 10KB of random junk, labeled application/octet-stream (generic, trigger sniff)
        var junkBytes = new byte[10240];
        new Random(42).NextBytes(junkBytes);
        _handler.Routes["http://test.local/badfile"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(junkBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
            }
        };

        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.True(ChunkCount(Seed) > 0);
        Assert.Equal(0, ChunkCount("http://test.local/badfile")); // skipped after prefix check fails
    }

    [Fact]
    public async Task Crawl_skips_non_docx_zip_files_by_extension()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><a href=\"/data.zip\">ZIP archive</a>");
        
        var zipBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00 };
        _handler.Routes["http://test.local/data.zip"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zipBytes)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
            }
        };

        await NewCrawler().CrawlAsync(Seed, maxPages: 5);

        Assert.True(ChunkCount(Seed) > 0);
        Assert.Equal(0, ChunkCount("http://test.local/data.zip")); // skipped because it's zip magic but extension is not docx
    }

    public void Dispose()
    {
        _provider.Dispose();
        _httpClient.Dispose();
        _handler.Dispose();
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* temp file; best effort */ }
    }

    /// <summary>Returns an XML 200, e.g. for a sitemap.</summary>
    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    /// <summary>Returns an HTML 200 with an optional strong ETag.</summary>
    private static HttpResponseMessage Html(string body, string? etag = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"<html><head>{body}</head><body>{body}</body></html>", Encoding.UTF8, "text/html"),
        };
        if (etag != null) resp.Headers.ETag = new EntityTagHeaderValue(etag);
        return resp;
    }

    /// <summary>Canned HTTP server: routes keyed by absolute URL; unmapped paths 404.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Requested = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.GetLeftPart(UriPartial.Query);
            Requested.Add(url);

            HttpResponseMessage response = Routes.TryGetValue(url, out var factory)
                ? factory(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound);

            response.RequestMessage ??= request; // crawler reads RequestMessage.RequestUri for redirects
            return Task.FromResult(response);
        }
    }
}
