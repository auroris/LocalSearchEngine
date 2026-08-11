using System.Net;
using System.Text;
using LocalSearchEngine.Core;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Searching;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Drives feed-bounded incremental runs through <see cref="CrawlerService.CrawlSitesAsync"/>. The
/// contract under test is the positive-indicator rule: a site's advertised feed is its change
/// journal, walked newest-first — entries not yet covered are the run, and the first entry whose
/// stored visit is at or after its own date proves everything older was seen, so the run stops
/// there. A feed that can't prove that (absent, unparseable, or its window ends before a covered
/// entry) must fall back to a full crawl, never to guessing.
/// </summary>
public sealed class IncrementalCrawlTests : IDisposable
{
    private const string Seed = "http://test.local/";
    private const string Robots = "http://test.local/robots.txt";
    private const string Feed = "http://test.local/rss.xml";

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ServiceProvider _provider;
    private readonly VectorSearchService _search;
    private readonly FakeEmbedder _embedder = new();
    private readonly CountingFakeHandler _handler = new();
    private readonly HttpClient _httpClient;

    public IncrementalCrawlTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lse_incr_{Guid.NewGuid():N}.db");
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

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"<html><head>{body}</head><body>{body}</body></html>", Encoding.UTF8, "text/html"),
    };

    /// <summary>The seed page: advertises the feed and links one ordinary page nothing else mentions.</summary>
    private static HttpResponseMessage HomeAdvertisingFeed() => Html(
        "<title>Home</title><p>home page</p>" +
        "<link rel=\"alternate\" type=\"application/rss+xml\" href=\"/rss.xml\">" +
        " <a href=\"/about\">about</a>");

    private static HttpResponseMessage Rss(params (string Url, DateTime PublishedUtc)[] items)
    {
        var xml = string.Concat(items.Select(i =>
            $"<item><title>t</title><link>{i.Url}</link><pubDate>{i.PublishedUtc:R}</pubDate></item>"));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>S</title>{xml}</channel></rss>",
                Encoding.UTF8, "application/rss+xml"),
        };
    }

    [Fact]
    public async Task Feed_proves_the_change_list_and_the_run_is_exactly_the_changes()
    {
        await EnsureSchemaAsync();
        var oldDate = DateTime.UtcNow.AddHours(-2);

        _handler.Routes[Seed] = _ => HomeAdvertisingFeed();
        _handler.Routes["http://test.local/about"] = _ => Html("<title>About</title><p>rarely changing page</p>");
        _handler.Routes["http://test.local/postA"] = _ => Html("<title>A</title><p>first post body</p>");
        _handler.Routes["http://test.local/postB"] = _ => Html("<title>B</title><p>second post body</p>");
        _handler.Routes[Feed] = _ => Rss(("http://test.local/postB", oldDate), ("http://test.local/postA", oldDate));

        await NewCrawler().CrawlSitesAsync(new[] { Seed });
        Assert.True(await ChunkCount("http://test.local/about") > 0);

        // A new post lands: it tops the feed, and the older entries — crawled after their dates —
        // are the boundary proving nothing else changed.
        var newDate = DateTime.UtcNow.AddHours(1);
        _handler.Routes["http://test.local/postC"] = _ => Html("<title>C</title><p>brand new post body</p>");
        _handler.Routes[Feed] = _ => Rss(
            ("http://test.local/postC", newDate),
            ("http://test.local/postB", oldDate),
            ("http://test.local/postA", oldDate));

        int before = _handler.RequestedCount;
        var report = await NewCrawler().CrawlSitesAsync(new[] { Seed }, allowIncremental: true);
        var secondRun = _handler.RequestedSince(before);

        Assert.True(await ChunkCount("http://test.local/postC") > 0);
        // Exactly: robots, the root-page probe, the feed, and the one changed item — no sitemap
        // sweep, no re-fetch of the site's other pages.
        Assert.Equal(
            new[] { Feed, Robots, Seed, "http://test.local/postC" }.OrderBy(u => u, StringComparer.Ordinal),
            secondRun.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(u => u, StringComparer.Ordinal));
        Assert.DoesNotContain("http://test.local/sitemap.xml", secondRun);
        Assert.Equal(report.EmbedQueued, report.EmbedProcessed);
    }

    [Fact]
    public async Task Feed_window_without_a_covered_entry_falls_back_to_a_full_crawl()
    {
        await EnsureSchemaAsync();

        // Empty database: nothing is covered, so however fresh the feed looks, it cannot prove the
        // change list complete — there may be older content it no longer lists.
        _handler.Routes[Seed] = _ => HomeAdvertisingFeed();
        _handler.Routes["http://test.local/about"] = _ => Html("<title>About</title><p>page the feed never lists</p>");
        _handler.Routes["http://test.local/postA"] = _ => Html("<title>A</title><p>first post body</p>");
        _handler.Routes[Feed] = _ => Rss(("http://test.local/postA", DateTime.UtcNow));

        await NewCrawler().CrawlSitesAsync(new[] { Seed }, allowIncremental: true);

        // The full-crawl fallback found the unlisted page through links.
        Assert.True(await ChunkCount("http://test.local/about") > 0);
        Assert.True(await ChunkCount("http://test.local/postA") > 0);
        Assert.Contains("http://test.local/sitemap.xml", _handler.RequestedSnapshot());
    }

    [Fact]
    public async Task Site_without_an_advertised_feed_falls_back_to_a_full_crawl()
    {
        await EnsureSchemaAsync();

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home page, no feed</p> <a href=\"/about\">about</a>");
        _handler.Routes["http://test.local/about"] = _ => Html("<title>About</title><p>about page body</p>");

        await NewCrawler().CrawlSitesAsync(new[] { Seed }, allowIncremental: true);

        Assert.True(await ChunkCount("http://test.local/about") > 0);
    }

    [Fact]
    public async Task Unchanged_feed_makes_the_run_three_requests()
    {
        await EnsureSchemaAsync();
        var oldDate = DateTime.UtcNow.AddHours(-2);

        _handler.Routes[Seed] = _ => HomeAdvertisingFeed();
        _handler.Routes["http://test.local/about"] = _ => Html("<title>About</title><p>about page body</p>");
        _handler.Routes["http://test.local/postA"] = _ => Html("<title>A</title><p>first post body</p>");
        _handler.Routes[Feed] = _ => Rss(("http://test.local/postA", oldDate));

        await NewCrawler().CrawlSitesAsync(new[] { Seed });
        int embedsAfterFull = _embedder.EmbedCount;

        int before = _handler.RequestedCount;
        await NewCrawler().CrawlSitesAsync(new[] { Seed }, allowIncremental: true);
        var secondRun = _handler.RequestedSince(before);

        // The feed's top entry is already covered: the incremental run is the three probes and
        // nothing else — this is what routine scheduled runs cost when nothing changed.
        Assert.Equal(
            new[] { Feed, Robots, Seed }.OrderBy(u => u, StringComparer.Ordinal),
            secondRun.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(u => u, StringComparer.Ordinal));
        Assert.Equal(embedsAfterFull, _embedder.EmbedCount);
    }

    [Fact]
    public async Task Updated_item_reappearing_atop_the_feed_is_recrawled()
    {
        await EnsureSchemaAsync();
        var oldDate = DateTime.UtcNow.AddHours(-2);

        _handler.Routes[Seed] = _ => HomeAdvertisingFeed();
        _handler.Routes["http://test.local/about"] = _ => Html("<title>About</title><p>about page body</p>");
        _handler.Routes["http://test.local/postA"] = _ => Html("<title>A</title><p>original body</p>");
        _handler.Routes["http://test.local/postB"] = _ => Html("<title>B</title><p>second post body</p>");
        _handler.Routes[Feed] = _ => Rss(("http://test.local/postA", oldDate), ("http://test.local/postB", oldDate));

        await NewCrawler().CrawlSitesAsync(new[] { Seed });
        int embedsAfterFull = _embedder.EmbedCount;

        // The old post is edited and re-listed at the top with a fresh date. A mere "URL already
        // indexed" test would stop right on it and miss the edit; the date-based coverage test
        // counts it as a change and bounds the run at the untouched entry below.
        _handler.Routes["http://test.local/postA"] = _ => Html("<title>A</title><p>revised body with corrections</p>");
        _handler.Routes[Feed] = _ => Rss(
            ("http://test.local/postA", DateTime.UtcNow.AddHours(1)),
            ("http://test.local/postB", oldDate));

        int before = _handler.RequestedCount;
        await NewCrawler().CrawlSitesAsync(new[] { Seed }, allowIncremental: true);
        var secondRun = _handler.RequestedSince(before);

        Assert.True(_embedder.EmbedCount > embedsAfterFull, "the edited item must re-embed");
        Assert.Contains("http://test.local/postA", secondRun);
        Assert.DoesNotContain("http://test.local/postB", secondRun);
    }

    [Fact]
    public async Task Declared_journal_bounds_the_run_across_hosts()
    {
        await EnsureSchemaAsync();
        var oldDate = DateTime.UtcNow.AddHours(-2);
        const string site1 = "http://site1.local/";
        const string site2 = "http://site2.local/";
        const string journal = "http://site1.local/changes.xml";
        const string newDoc = "http://site2.local/docs/new-report.html";

        // The coldlake shape: a page host and a document host, where only the page host can serve
        // a feed and the document host cannot advertise anything. The journal is configured, not
        // discovered — it is not linked or advertised anywhere.
        _handler.Routes[site1] = _ => Html("<title>One</title><p>site one home</p>");
        _handler.Routes[site2] = _ => Html("<title>Two</title><p>document host listing</p>");
        await NewCrawler().CrawlSitesAsync(new[] { site1, site2 });

        _handler.Routes[newDoc] = _ => Html("<title>New Report</title><p>freshly uploaded document page</p>");
        _handler.Routes[journal] = _ => Rss(
            (newDoc, DateTime.UtcNow.AddHours(1)),   // the change, on the host that has no feed
            (site1, oldDate));                        // covered tail entry: the boundary

        int before = _handler.RequestedCount;
        await NewCrawler().CrawlSitesAsync(
            new[] { site1, site2 }, allowIncremental: true, incrementalFeed: journal);
        var secondRun = _handler.RequestedSince(before);

        Assert.True(await ChunkCount(newDoc) > 0);
        // Exactly: both robots probes, the journal, and the one cross-host change. No root-page
        // probes (declared mode needs no autodiscovery), no sitemap sweep, no boundary re-fetch.
        Assert.Equal(
            new[] { site1 + "robots.txt", site2 + "robots.txt", journal, newDoc }.OrderBy(u => u, StringComparer.Ordinal),
            secondRun.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(u => u, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Declared_journal_quiet_day_is_three_requests()
    {
        await EnsureSchemaAsync();
        var oldDate = DateTime.UtcNow.AddHours(-2);
        const string journal = "http://test.local/changes.xml";

        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home page</p>");
        await NewCrawler().CrawlSitesAsync(new[] { Seed });

        // Nothing changed in the window; the journal's overlap tail alone bounds the run instantly.
        _handler.Routes[journal] = _ => Rss((Seed, oldDate));

        int before = _handler.RequestedCount;
        await NewCrawler().CrawlSitesAsync(new[] { Seed }, allowIncremental: true, incrementalFeed: journal);
        var secondRun = _handler.RequestedSince(before);

        Assert.Equal(
            new[] { journal, Robots }.OrderBy(u => u, StringComparer.Ordinal),
            secondRun.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(u => u, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Declared_journal_without_a_covered_entry_falls_back_to_full()
    {
        await EnsureSchemaAsync();
        const string journal = "http://test.local/changes.xml";

        // Empty database: even a fresh-looking journal proves nothing — its window may have rolled
        // past changes we never saw.
        _handler.Routes[Seed] = _ => Html("<title>Home</title><p>home page</p> <a href=\"/about\">about</a>");
        _handler.Routes["http://test.local/about"] = _ => Html("<title>About</title><p>page the journal never lists</p>");
        _handler.Routes[journal] = _ => Rss(("http://test.local/about", DateTime.UtcNow));

        await NewCrawler().CrawlSitesAsync(new[] { Seed }, allowIncremental: true, incrementalFeed: journal);

        Assert.True(await ChunkCount(Seed) > 0, "the full-crawl fallback must run");
        Assert.Contains("http://test.local/sitemap.xml", _handler.RequestedSnapshot());
    }

    [Fact]
    public async Task Multiple_sites_crawl_in_one_run()
    {
        await EnsureSchemaAsync();

        _handler.Routes["http://site1.local/"] = _ => Html("<title>One</title><p>site one home</p>");
        _handler.Routes["http://site2.local/"] = _ => Html("<title>Two</title><p>site two home</p>");

        await NewCrawler().CrawlSitesAsync(new[] { "http://site1.local/", "http://site2.local/" });

        Assert.True(await ChunkCount("http://site1.local/") > 0);
        Assert.True(await ChunkCount("http://site2.local/") > 0);

        // A second identical run must not prune either site: both are visited in the same pass, so
        // the single-run prune sees the whole in-scope world.
        await NewCrawler().CrawlSitesAsync(new[] { "http://site1.local/", "http://site2.local/" });
        Assert.True(await ChunkCount("http://site1.local/") > 0);
        Assert.True(await ChunkCount("http://site2.local/") > 0);
    }

    /// <summary>
    /// Canned HTTP server: routes keyed by absolute URL; unmapped paths 404. The request log locks
    /// and supports slicing, so a test can isolate what a particular run fetched.
    /// </summary>
    private sealed class CountingFakeHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _requested = new();

        public int RequestedCount { get { lock (_requested) { return _requested.Count; } } }

        public IReadOnlyList<string> RequestedSnapshot()
        {
            lock (_requested) { return _requested.ToArray(); }
        }

        public IReadOnlyList<string> RequestedSince(int start)
        {
            lock (_requested) { return _requested.Skip(start).ToArray(); }
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
