using System.Diagnostics;
using System.Net;
using System.Text;
using LocalSearchEngine.Core;
using LocalSearchEngine.Core.Crawling.Engine;
using LocalSearchEngine.Core.Crawling.Pipeline;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Crawling.Storage;
using LocalSearchEngine.Core.Searching;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Drives the channel pipeline directly (no CrawlerService facade) against a fake HTTP server and a
/// real sqlite-vec database. These tests exist for the properties the old engine never had to prove:
/// that a self-feeding channel crawl terminates — on deep chains, wide fanouts, redirect chains, a
/// zero-work seed, and a mid-flight cap — and that four workers index every page exactly once.
/// </summary>
public sealed class CrawlPipelineTests : IDisposable
{
    private const string Seed = "http://test.local/";

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ServiceProvider _provider;
    private readonly VectorSearchService _search;
    private readonly FakeEmbedder _embedder = new();
    private readonly PipelineFakeHandler _handler = new();
    private readonly HttpClient _httpClient;

    public CrawlPipelineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lse_pipeline_{Guid.NewGuid():N}.db");
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

    private async Task EnsureSchemaAsync()
    {
        await _search.EnsureCreatedAsync();
        await CrawlStore.EnsureSchemaAsync(_connectionString);
    }

    /// <summary>
    /// Runs one pipeline crawl of <paramref name="seedUrl"/> under a hang guard: a crawl that fails
    /// to terminate is the bug class these tests exist for, so it must fail fast and loudly rather
    /// than time out the whole test run.
    /// </summary>
    private async Task<(PipelineResult Result, EmbeddingBacklog Backlog)> RunAsync(
        string seedUrl,
        int workers = 4,
        int maxPages = int.MaxValue,
        bool followLinks = true,
        int requestDelayMs = 0,
        bool includeSitemapSource = true)
    {
        var seedUri = new Uri(seedUrl);
        var scope = new AllowedHosts();
        scope.AddOrigin(seedUri);
        var hostHealth = new HostHealthTracker();
        var robots = new RobotsDirectory(_httpClient, hostHealth, 15 * 1024 * 1024, NullLogger.Instance);
        var heartbeat = new CrawlHeartbeat();
        var observer = new CrawlObserver(NullLogger.Instance, NullCrawlReporter.Instance, DateTime.UtcNow, heartbeat);
        var backlog = new EmbeddingBacklog();

        var sources = new List<ISeedSource>();
        if (includeSitemapSource)
        {
            sources.Add(new SitemapSeedSource(seedUri, robots));
        }
        sources.Add(new RootUrlSource(seedUri, robots));

        var plan = new CrawlPlan
        {
            SeedUris = new[] { seedUri },
            SeedSources = sources,
            Scope = scope,
            FollowLinks = followLinks,
            CrawlWorkers = workers,
            MaxPages = maxPages,
            DefaultRequestDelayMs = requestDelayMs,
        };

        await using var write = new SqliteConnection(_connectionString);
        await write.OpenAsync();

        var pipeline = new CrawlPipeline(plan, _httpClient, _search, _connectionString, write,
            robots, hostHealth, observer, heartbeat, NullCrawlReporter.Instance, backlog, NullLogger.Instance);
        observer.DiscoveredCount = () => pipeline.Visited.Count;

        using var hangGuard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await pipeline.RunAsync(hangGuard.Token);
        return (result, backlog);
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

    private async Task<bool> HasCrawlState(string url)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM CrawlState WHERE Url = @u";
        cmd.Parameters.AddWithValue("@u", url);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    private async Task<List<string>> StoredOutlinks(string fromUrl)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return await CrawlStore.GetStoredOutlinksAsync(connection, fromUrl);
    }

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"<html><head>{body}</head><body>{body}</body></html>", Encoding.UTF8, "text/html"),
    };

    private static HttpResponseMessage RedirectedTo(string targetUrl, string body)
    {
        var resp = Html(body);
        resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        return resp;
    }

    [Fact]
    public async Task Four_workers_index_every_page_exactly_once()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home page</p> <a href=\"/a\">a</a> <a href=\"/b\">b</a> <a href=\"/c\">c</a>");
        _handler.Routes["http://test.local/a"] = _ => Html("<title>A</title><p>alpha content</p> <a href=\"/d\">d</a>");
        _handler.Routes["http://test.local/b"] = _ => Html("<title>B</title><p>beta content</p>");
        _handler.Routes["http://test.local/c"] = _ => Html("<title>C</title><p>gamma content</p>");
        _handler.Routes["http://test.local/d"] = _ => Html("<title>D</title><p>delta content</p>");

        var (result, backlog) = await RunAsync(Seed);

        Assert.Equal(5, result.IndexedCount);
        Assert.False(result.CappedWithWorkRemaining);
        Assert.False(result.HostCapSkipped);
        Assert.Equal(backlog.Queued, backlog.Processed);
        foreach (var url in new[] { Seed, "http://test.local/a", "http://test.local/b", "http://test.local/c", "http://test.local/d" })
        {
            Assert.True(await ChunkCount(url) > 0, $"expected chunks for {url}");
            Assert.True(await HasCrawlState(url), $"expected crawl state for {url}");
        }

        // Every page fetched exactly once: 4 workers must not double-fetch through the seen-gate.
        var requested = _handler.RequestedSnapshot();
        foreach (var url in new[] { Seed, "http://test.local/a", "http://test.local/b", "http://test.local/c", "http://test.local/d" })
        {
            Assert.Equal(1, requested.Count(r => string.Equals(r, url, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public async Task Zero_work_crawl_terminates()
    {
        await EnsureSchemaAsync();

        // Everything 404s, and robots disallows the seed — the frontier only ever holds the
        // (404ing) conventional sitemap probe. Termination must come from the refcount, not luck.
        _handler.Routes["http://test.local/robots.txt"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("User-agent: *\nDisallow: /", Encoding.UTF8, "text/plain"),
        };

        var (result, _) = await RunAsync(Seed);

        Assert.Equal(0, result.IndexedCount);
        Assert.Equal(0, result.JobsSubmitted);
    }

    [Fact]
    public async Task Deep_chain_completes()
    {
        await EnsureSchemaAsync();

        const int depth = 30;
        _handler.Routes[Seed] = _ => Html("<title>P0</title><p>page zero</p> <a href=\"/p1\">next</a>");
        for (int i = 1; i <= depth; i++)
        {
            int n = i;
            string next = n < depth ? $" <a href=\"/p{n + 1}\">next</a>" : string.Empty;
            _handler.Routes[$"http://test.local/p{n}"] = _ => Html($"<title>P{n}</title><p>page number {n} content</p>{next}");
        }

        var (result, _) = await RunAsync(Seed);

        Assert.Equal(depth + 1, result.IndexedCount);
    }

    [Fact]
    public async Task Wide_fanout_completes_and_indexes_all()
    {
        await EnsureSchemaAsync();

        const int fanout = 150;
        var links = new StringBuilder();
        for (int i = 0; i < fanout; i++)
        {
            links.Append($" <a href=\"/leaf{i}\">leaf {i}</a>");
        }
        _handler.Routes[Seed] = _ => Html($"<title>Hub</title><p>hub page</p>{links}");
        for (int i = 0; i < fanout; i++)
        {
            int n = i;
            _handler.Routes[$"http://test.local/leaf{n}"] = _ => Html($"<title>Leaf {n}</title><p>unique leaf content number {n}</p>");
        }

        var (result, backlog) = await RunAsync(Seed);

        Assert.Equal(fanout + 1, result.IndexedCount);
        Assert.Equal(backlog.Queued, backlog.Processed);
    }

    [Fact]
    public async Task Redirect_chain_is_followed_and_sources_recorded_as_aliases()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/r1\">moved</a>");
        _handler.Routes["http://test.local/r1"] = _ => RedirectedTo("http://test.local/r2", "<title>hop</title><p>hop</p>");
        _handler.Routes["http://test.local/r2"] = _ => RedirectedTo("http://test.local/final", "<title>hop</title><p>hop</p>");
        _handler.Routes["http://test.local/final"] = _ => Html("<title>Final</title><p>the destination page</p>");

        var (result, _) = await RunAsync(Seed);

        Assert.Equal(2, result.IndexedCount); // home + final
        Assert.True(await ChunkCount("http://test.local/final") > 0);
        Assert.Equal(0, await ChunkCount("http://test.local/r1"));
        Assert.Equal(0, await ChunkCount("http://test.local/r2"));
        Assert.True(await HasCrawlState("http://test.local/r1"), "redirect source keeps a crawl-state row");
    }

    [Fact]
    public async Task MaxPages_cap_stops_the_crawl_and_forfeits_natural_completion()
    {
        await EnsureSchemaAsync();

        const int fanout = 60;
        const int cap = 5;
        const int workers = 4;
        var links = new StringBuilder();
        for (int i = 0; i < fanout; i++)
        {
            links.Append($" <a href=\"/deep{i}\">deep {i}</a>");
        }
        _handler.Routes[Seed] = _ => Html($"<title>Hub</title><p>hub</p>{links}");
        for (int i = 0; i < fanout; i++)
        {
            int n = i;
            _handler.Routes[$"http://test.local/deep{n}"] = _ => Html($"<title>Deep {n}</title><p>deep page number {n}</p>");
        }

        var (result, _) = await RunAsync(Seed, workers: workers, maxPages: cap);

        // Workers can all have candidates in flight when the cap is reached, but the shared
        // acceptance decision must reserve exactly cap slots and reject every later candidate.
        Assert.Equal(cap, result.IndexedCount);
        Assert.True(result.CappedWithWorkRemaining, "a capped run with work left must not count as completed naturally");
    }

    [Fact]
    public async Task Same_host_fetches_respect_the_politeness_gap_across_workers()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home</p> <a href=\"/x\">x</a> <a href=\"/y\">y</a>");
        _handler.Routes["http://test.local/x"] = _ => Html("<title>X</title><p>x page content</p>");
        _handler.Routes["http://test.local/y"] = _ => Html("<title>Y</title><p>y page content</p>");

        var sw = Stopwatch.StartNew();
        var (result, _) = await RunAsync(Seed, requestDelayMs: 150, includeSitemapSource: false);
        sw.Stop();

        Assert.Equal(3, result.IndexedCount);
        // Three page fetches on one host = two enforced gaps minimum, no matter how many workers.
        Assert.True(sw.ElapsedMilliseconds >= 300,
            $"Crawl finished in {sw.ElapsedMilliseconds}ms; the per-host gap is not being enforced across workers.");
    }

    [Fact]
    public async Task Sitemap_entries_seed_the_frontier()
    {
        await EnsureSchemaAsync();

        _handler.Routes["http://test.local/sitemap.xml"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\"?><urlset><url><loc>http://test.local/only-in-sitemap</loc></url></urlset>",
                Encoding.UTF8, "application/xml"),
        };
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home, links nothing</p>");
        _handler.Routes["http://test.local/only-in-sitemap"] = _ => Html("<title>Hidden</title><p>reachable only through the sitemap</p>");

        var (result, _) = await RunAsync(Seed);

        Assert.Equal(2, result.IndexedCount);
        Assert.True(await ChunkCount("http://test.local/only-in-sitemap") > 0);
    }

    [Fact]
    public async Task Href_escaping_survives_to_the_wire_while_the_database_stores_display_form()
    {
        await EnsureSchemaAsync();

        // The bug class this pins: filesystem-listing hrefs whose percent-encoding must reach the
        // server byte-exact. Routes are keyed on the *request's* escaped URI, so a hit proves the
        // wire form; the database must meanwhile hold the normalized display form, unchanged from
        // the old engine, so existing rows keep matching. A trailing-slash href must also go out
        // exactly as written — the old engine trimmed it on the wire and ate a redirect (or a 404)
        // on servers that distinguish the two.
        _handler.Routes[Seed] = _ => Html(
            "<title>Docs</title><p>listing</p>" +
            " <a href=\"/caf%C3%A9/menu.html\">menu</a>" +
            " <a href=\"/a%20b.html\">spaced</a>" +
            " <a href=\"/reports/2024/\">reports</a>");
        _handler.Routes["http://test.local/caf%C3%A9/menu.html"] = _ => Html("<title>Menu</title><p>the cafe menu page</p>");
        _handler.Routes["http://test.local/a%20b.html"] = _ => Html("<title>Spaced</title><p>a spaced filename page</p>");
        _handler.Routes["http://test.local/reports/2024/"] = _ => Html("<title>Reports</title><p>the reports listing page</p>");

        var (first, _) = await RunAsync(Seed, includeSitemapSource: false);
        Assert.Equal(4, first.IndexedCount);

        var requested = _handler.RequestedSnapshot();
        Assert.Contains("http://test.local/caf%C3%A9/menu.html", requested);
        Assert.Contains("http://test.local/a%20b.html", requested);
        Assert.Contains("http://test.local/reports/2024/", requested);

        // Stored identity is the display form (frozen contract with existing databases).
        Assert.True(await HasCrawlState("http://test.local/café/menu.html"));
        Assert.True(await HasCrawlState("http://test.local/a b.html"));
        Assert.True(await HasCrawlState("http://test.local/reports/2024"));

        // A second crawl must resolve to the same identities: same rows updated, no duplicates,
        // and unchanged content skips re-embedding via the stored hash.
        var (second, backlog) = await RunAsync(Seed, includeSitemapSource: false);
        Assert.Equal(0, second.IndexedCount);
        Assert.Equal(backlog.Queued, backlog.Processed);
        Assert.Equal(1, await CrawlStateRowCount("http://test.local/café/menu.html"));
        Assert.Equal(1, await CrawlStateRowCount("http://test.local/a b.html"));
        Assert.Equal(1, await CrawlStateRowCount("http://test.local/reports/2024"));
    }

    [Fact]
    public async Task Unchanged_visible_text_still_refreshes_link_destinations_and_context()
    {
        await EnsureSchemaAsync();
        const string oldTarget = "http://test.local/old-target";
        const string newTarget = "http://test.local/new-target";

        _handler.Routes[Seed] = _ => Html(
            "<title>Home</title><h2>Procedures</h2><p>Use the <a href=\"/old-target\">maintenance guide</a>.</p>");
        _handler.Routes[oldTarget] = _ => Html("<title>Old</title><p>old target body</p>");
        _handler.Routes[newTarget] = _ => Html("<title>New</title><p>new target body</p>");

        await RunAsync(Seed, includeSitemapSource: false);
        Assert.Equal([oldTarget], await StoredOutlinks(Seed));

        // Only href changes. Title, headings, visible body, and therefore ContentHash are identical,
        // so the seed takes the parsed TouchJob path rather than being re-embedded.
        _handler.Routes[Seed] = _ => Html(
            "<title>Home</title><h2>Procedures</h2><p>Use the <a href=\"/new-target\">maintenance guide</a>.</p>");

        await RunAsync(Seed, includeSitemapSource: false);

        Assert.Equal([newTarget], await StoredOutlinks(Seed));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ToUrl FROM LinkContexts WHERE FromUrl = @From";
        command.Parameters.AddWithValue("@From", Seed);
        Assert.Equal(newTarget, Convert.ToString(await command.ExecuteScalarAsync()));
    }

    private async Task<int> CrawlStateRowCount(string url)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM CrawlState WHERE Url = @u";
        cmd.Parameters.AddWithValue("@u", url);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Advertised_feed_is_consulted_and_pulls_unlinked_items()
    {
        await EnsureSchemaAsync();

        // The seed links to nothing, but advertises its feed; the feed names a post no page links
        // to. The positive-indicator contract: whatever the feed lists is guaranteed into the
        // frontier, even where link discovery would never find it.
        _handler.Routes[Seed] = _ => Html(
            "<title>Home</title><p>home page, links nothing</p>" +
            "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"/rss.xml\">");
        _handler.Routes["http://test.local/rss.xml"] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>S</title>" +
                "<item><title>t</title><link>http://test.local/unlinked-post</link></item>" +
                "</channel></rss>",
                Encoding.UTF8, "application/rss+xml"),
        };
        _handler.Routes["http://test.local/unlinked-post"] = _ => Html("<title>Hidden</title><p>reachable only through the advertised feed</p>");

        var (result, _) = await RunAsync(Seed, includeSitemapSource: false);

        Assert.Equal(2, result.IndexedCount);
        Assert.True(await ChunkCount("http://test.local/unlinked-post") > 0);
        // Advertised on the seed (which the Html helper mirrors into head and body) yet fetched once.
        Assert.Equal(1, _handler.RequestedSnapshot().Count(r => r.Equals("http://test.local/rss.xml", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Discovered_feed_budget_bounds_feed_fetches()
    {
        await EnsureSchemaAsync();

        // Blog platforms advertise a distinct comments feed on every post; the per-run budget keeps
        // a large site from spending hundreds of fetches re-learning URLs it already knows.
        const int advertised = 12;
        const int budget = 8; // CrawlPipeline.MaxDiscoveredFeeds
        var links = new StringBuilder();
        for (int i = 0; i < advertised; i++)
        {
            links.Append($"<link rel=\"alternate\" type=\"application/rss+xml\" href=\"/feed{i}.xml\">");
        }
        _handler.Routes[Seed] = _ => Html($"<title>Home</title><p>home page</p>{links}");
        // The feeds themselves 404 — the budget counts fetch attempts, not parse successes.

        var (result, _) = await RunAsync(Seed, includeSitemapSource: false);

        Assert.Equal(1, result.IndexedCount);
        int feedFetches = _handler.RequestedSnapshot().Count(r => r.Contains("/feed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(budget, feedFetches);
    }

    /// <summary>
    /// Canned HTTP server for pipeline tests: routes keyed by absolute URL; unmapped paths 404.
    /// Requests arrive from concurrent workers, so the request log locks.
    /// </summary>
    private sealed class PipelineFakeHandler : HttpMessageHandler
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

            response.RequestMessage ??= request; // the downloader reads RequestMessage.RequestUri for redirects
            return Task.FromResult(response);
        }
    }
}
