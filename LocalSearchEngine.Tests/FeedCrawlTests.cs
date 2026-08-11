using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalSearchEngine.Core;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Searching;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Xunit;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Drives feed-driven update crawls end to end through <see cref="CrawlerService.CrawlFeedAsync"/>.
/// The contract under test is the update run's promise: fetch exactly what the feed lists (plus the
/// origin's robots.txt), re-embed only what changed, follow nothing, and never delete anything the
/// run didn't visit — so a site's rss.xml can drive cheap incremental indexing between full crawls.
/// </summary>
public sealed class FeedCrawlTests : IDisposable
{
    private const string Feed = "http://test.local/rss.xml";
    private const string Robots = "http://test.local/robots.txt";
    private const string Post1 = "http://test.local/post1";
    private const string Post2 = "http://test.local/post2";

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ServiceProvider _provider;
    private readonly VectorSearchService _search;
    private readonly FakeEmbedder _embedder = new();
    private readonly FeedFakeHandler _handler = new();
    private readonly HttpClient _httpClient;

    public FeedCrawlTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lse_feed_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";

        var services = new ServiceCollection();
        services.AddSqliteVectorStore(_ => _connectionString);
        _provider = services.BuildServiceProvider();

        var store = _provider.GetRequiredService<VectorStore>();
        var settings = Options.Create(new SearchSettings { MaxDistance = 1.0, CandidatePoolSize = 100 });
        _search = new VectorSearchService(_embedder, store, new DatabaseConfig(_connectionString), settings, NullLogger<VectorSearchService>.Instance);
        _httpClient = new HttpClient(_handler);
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

    private CrawlerService NewCrawler() =>
        new(_httpClient, _search, NullLogger<CrawlerService>.Instance, new DatabaseConfig(_connectionString));

    private async Task EnsureSchemaAsync()
    {
        await _search.EnsureCreatedAsync();
        await NewCrawler().EnsureCreatedAsync();
    }

    private async Task<int> ChunkCount(string url)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM text_chunks WHERE Url = @u";
        cmd.Parameters.AddWithValue("@u", url);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static HttpResponseMessage Html(string body, string? etag = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"<html><head>{body}</head><body>{body}</body></html>", Encoding.UTF8, "text/html"),
        };
        if (etag != null) resp.Headers.ETag = new EntityTagHeaderValue(etag);
        return resp;
    }

    /// <summary>A page that answers 304 to a matching If-None-Match and a full 200 (with ETag) otherwise.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> ConditionalHtml(string body, string etag) => request =>
        request.Headers.IfNoneMatch.Any(t => t.Tag == etag)
            ? new HttpResponseMessage(HttpStatusCode.NotModified)
            : Html(body, etag);

    private static HttpResponseMessage Rss(params string[] itemLinks)
    {
        var items = string.Concat(itemLinks.Select(l => $"<item><title>t</title><link>{l}</link></item>"));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>Site</title><link>http://test.local/</link>{items}</channel></rss>",
                Encoding.UTF8, "application/rss+xml"),
        };
    }

    [Fact]
    public async Task Update_run_touches_exactly_the_feed_and_its_items()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Feed] = _ => Rss(Post1, Post2);
        // The items link onward; an update run must not follow those links.
        _handler.Routes[Post1] = _ => Html("<title>One</title><p>first post body</p> <a href=\"/elsewhere\">more</a>");
        _handler.Routes[Post2] = _ => Html("<title>Two</title><p>second post body</p>");
        _handler.Routes["http://test.local/elsewhere"] = _ => Html("<title>Elsewhere</title><p>must not be fetched</p>");

        var report = await NewCrawler().CrawlFeedAsync(Feed);

        Assert.True(await ChunkCount(Post1) > 0);
        Assert.True(await ChunkCount(Post2) > 0);
        Assert.Equal(0, await ChunkCount("http://test.local/elsewhere"));

        var requested = _handler.RequestedSnapshot();
        Assert.Equal(
            new[] { Feed, Post1, Post2, Robots }.OrderBy(u => u, StringComparer.Ordinal),
            requested.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(u => u, StringComparer.Ordinal));

        Assert.Equal(report.EmbedQueued, report.EmbedProcessed);
    }

    [Fact]
    public async Task Update_run_never_deletes_what_it_did_not_visit()
    {
        await EnsureSchemaAsync();

        // Full crawl first: the site has a page the feed will never mention.
        _handler.Routes["http://test.local/"] = _ => Html("<title>Home</title><p>home page</p> <a href=\"/orphan\">orphan</a> <a href=\"/post1\">post</a>");
        _handler.Routes["http://test.local/orphan"] = _ => Html("<title>Orphan</title><p>rarely-changing page the feed never lists</p>");
        _handler.Routes[Post1] = _ => Html("<title>One</title><p>first post body</p>");
        await NewCrawler().CrawlAsync("http://test.local/");
        Assert.True(await ChunkCount("http://test.local/orphan") > 0);

        // The site's robots policy changes between runs too. A feed update remains partial: neither
        // stale pruning nor the global robots-ban cleanup may delete an unlisted historical page.
        _handler.Routes[Robots] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("User-agent: *\nDisallow: /orphan", Encoding.UTF8, "text/plain"),
        };

        // Update run listing only post1. The orphan (and the home page) must survive untouched.
        _handler.Routes[Feed] = _ => Rss(Post1);
        var report = await NewCrawler().CrawlFeedAsync(Feed);

        Assert.True(await ChunkCount("http://test.local/orphan") > 0, "update run pruned a page it never visited");
        Assert.True(await ChunkCount("http://test.local/") > 0);
        // CompletedNaturally means "drained its frontier", and a clean update run did — pruning is
        // suppressed by the update plan itself (PruneStale off), never by this flag.
        Assert.True(report.CompletedNaturally);
    }

    [Fact]
    public async Task Unchanged_item_costs_a_304_and_no_reembedding()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Feed] = _ => Rss(Post1);
        _handler.Routes[Post1] = ConditionalHtml("<title>One</title><p>stable post body</p>", "\"v1\"");

        await NewCrawler().CrawlFeedAsync(Feed);
        int embedsAfterFirst = _embedder.EmbedCount;
        Assert.True(await ChunkCount(Post1) > 0);

        await NewCrawler().CrawlFeedAsync(Feed);

        Assert.Equal(embedsAfterFirst, _embedder.EmbedCount);
        Assert.True(await ChunkCount(Post1) > 0);
    }

    [Fact]
    public async Task Changed_item_is_reembedded()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Feed] = _ => Rss(Post1);
        _handler.Routes[Post1] = _ => Html("<title>One</title><p>original body</p>");
        await NewCrawler().CrawlFeedAsync(Feed);
        int embedsAfterFirst = _embedder.EmbedCount;

        _handler.Routes[Post1] = _ => Html("<title>One</title><p>revised body with new content</p>");
        await NewCrawler().CrawlFeedAsync(Feed);

        Assert.True(_embedder.EmbedCount > embedsAfterFirst, "a changed item must re-embed");
    }

    [Fact]
    public async Task Item_gone_from_the_site_is_removed_from_the_index()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Feed] = _ => Rss(Post1);
        _handler.Routes[Post1] = _ => Html("<title>One</title><p>first post body</p>");
        await NewCrawler().CrawlFeedAsync(Feed);
        Assert.True(await ChunkCount(Post1) > 0);

        _handler.Routes.Remove(Post1); // now 404s
        await NewCrawler().CrawlFeedAsync(Feed);

        Assert.Equal(0, await ChunkCount(Post1));
    }

    [Fact]
    public async Task Atom_entries_are_enqueued_including_relative_hrefs()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Feed] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\"?><atom:feed xmlns:atom=\"http://www.w3.org/2005/Atom\">" +
                $"<atom:entry><atom:title>One</atom:title><atom:link rel=\"alternate\" href=\"{Post1}\"/></atom:entry>" +
                "<atom:entry><atom:title>Two</atom:title><atom:link href=\"/post2\"/></atom:entry>" +
                "<atom:entry><atom:title>Enclosure</atom:title><atom:link rel=\"enclosure\" href=\"http://test.local/audio.mp3\"/></atom:entry>" +
                "</atom:feed>",
                Encoding.UTF8, "application/atom+xml"),
        };
        _handler.Routes[Post1] = _ => Html("<title>One</title><p>first post body</p>");
        _handler.Routes[Post2] = _ => Html("<title>Two</title><p>second post body</p>");

        await NewCrawler().CrawlFeedAsync(Feed);

        Assert.True(await ChunkCount(Post1) > 0);
        Assert.True(await ChunkCount(Post2) > 0, "a relative Atom href must resolve against the feed URL");
        Assert.DoesNotContain("http://test.local/audio.mp3", _handler.RequestedSnapshot());
    }

    [Fact]
    public async Task Feed_seed_redirect_adopts_the_canonical_origin()
    {
        await EnsureSchemaAsync();

        const string canonicalFeed = "https://www.test.local/rss.xml";
        const string canonicalPost = "https://www.test.local/post1";

        // The fake handler represents HttpClient's already-followed redirect by setting the final
        // RequestMessage URI, matching the redirect simulation used by the crawler integration tests.
        _handler.Routes[Feed] = _ =>
        {
            var response = Rss();
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, canonicalFeed);
            return response;
        };
        _handler.Routes[canonicalFeed] = _ => Rss(canonicalPost);
        _handler.Routes[canonicalPost] = _ => Html("<title>Canonical</title><p>post on the canonical origin</p>");

        await NewCrawler().CrawlFeedAsync(Feed);

        Assert.True(await ChunkCount(canonicalPost) > 0);
        Assert.Contains(canonicalFeed, _handler.RequestedSnapshot());
    }

    [Fact]
    public async Task Hostile_or_malformed_feeds_fail_safely()
    {
        await EnsureSchemaAsync();

        // A DOCTYPE (the XXE vector) must be rejected by the hardened parser, and junk must not
        // crash the run — both cases end as a clean, empty update.
        _handler.Routes[Feed] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\"?><!DOCTYPE rss [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><rss version=\"2.0\"><channel><item><link>&xxe;</link></item></channel></rss>",
                Encoding.UTF8, "application/rss+xml"),
        };
        var report = await NewCrawler().CrawlFeedAsync(Feed);
        Assert.Equal(0, report.EmbedQueued);

        _handler.Routes[Feed] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("this is not xml at all", Encoding.UTF8, "application/rss+xml"),
        };
        report = await NewCrawler().CrawlFeedAsync(Feed);
        Assert.Equal(0, report.EmbedQueued);
    }

    [Fact]
    public async Task Out_of_scope_feed_items_are_not_fetched()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Feed] = _ => Rss(Post1, "http://evil.example/tracker");
        _handler.Routes[Post1] = _ => Html("<title>One</title><p>first post body</p>");

        await NewCrawler().CrawlFeedAsync(Feed);

        Assert.True(await ChunkCount(Post1) > 0);
        Assert.DoesNotContain("http://evil.example/tracker", _handler.RequestedSnapshot());
    }

    /// <summary>
    /// Canned HTTP server: routes keyed by absolute URL; unmapped paths 404. Requests arrive from
    /// concurrent workers, so the request log locks.
    /// </summary>
    private sealed class FeedFakeHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _requested = new();

        public IReadOnlyList<string> RequestedSnapshot()
        {
            lock (_requested)
            {
                return _requested.ToArray();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.GetLeftPart(UriPartial.Query);
            lock (_requested)
            {
                _requested.Add(url);
            }

            HttpResponseMessage response = Routes.TryGetValue(url, out var factory)
                ? factory(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound);

            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
