using LocalSearchEngine.Core;
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
    private readonly CountingEmbedder _embedder = new();
    private readonly FakeHandler _handler = new();

    public CrawlerServiceIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lse_crawl_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";

        var services = new ServiceCollection();
        services.AddSqliteVectorStore(_ => _connectionString);
        _provider = services.BuildServiceProvider();

        var store = _provider.GetRequiredService<VectorStore>();
        var settings = Options.Create(new SearchSettings { MinSimilarity = 0.0, CandidatePoolSize = 100 });
        _search = new VectorSearchService(_embedder, store, new DatabaseConfig(_connectionString), settings, NullLogger<VectorSearchService>.Instance);
    }

    private CrawlerService NewCrawler() =>
        new(new HttpClient(_handler), _search, NullLogger<CrawlerService>.Instance, new DatabaseConfig(_connectionString));

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

    private long ChunkCount(string url)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM text_chunks WHERE Url = @u";
        command.Parameters.AddWithValue("@u", url);
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public void Dispose()
    {
        _provider.Dispose();
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

    /// <summary>Deterministic embedder that counts how many passages it has embedded.</summary>
    private sealed class CountingEmbedder : IEmbedder
    {
        public int EmbedCount { get; private set; }

        public ReadOnlyMemory<float> EmbedQuery(string text) => Vector(text);

        public ReadOnlyMemory<float> Embed(string text)
        {
            EmbedCount++;
            return Vector(text);
        }

        private static ReadOnlyMemory<float> Vector(string text)
        {
            ulong hash = 1469598103934665603UL;
            foreach (char ch in text)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }

            var vector = new float[384];
            ulong state = hash | 1UL;
            double sumSq = 0;
            for (int i = 0; i < vector.Length; i++)
            {
                state = state * 6364136223846793005UL + 1442695040888963407UL;
                double value = ((state >> 33) / (double)(1UL << 31)) - 1.0;
                vector[i] = (float)value;
                sumSq += value * value;
            }

            double norm = Math.Sqrt(sumSq);
            if (norm > 0)
            {
                for (int i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
            }
            return vector;
        }
    }
}
