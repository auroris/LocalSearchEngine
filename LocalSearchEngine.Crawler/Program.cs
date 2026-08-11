// Entry point for the LocalSearchEngine crawler — a command-line tool that crawls a site from a
// seed URL, extracts page content, generates embeddings locally, and stores them in a SQLite
// vector database for the web app to search. This file handles the console concerns: parsing
// options (CLI flags and appsettings.json), wiring up dependency injection, and running the crawl
// behind a live or plain progress reporter. The crawl itself lives in CrawlerService.

using LocalSearchEngine.Core;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.TextProcessing;
using LocalSearchEngine.Crawler;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using System.Net;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("../appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .Build();
var crawlSettings = config.GetSection("CrawlSettings").Get<CrawlSettings>() ?? new CrawlSettings();

string url = "";
string dbPath = !string.IsNullOrWhiteSpace(config["db"]) ? config["db"]! : "search.db";
int maxPages = crawlSettings.MaxPages ?? int.MaxValue;
int maxPagesPerHost = crawlSettings.MaxPagesPerHost ?? int.MaxValue;
long maxCrawlSizeBytes = crawlSettings.MaxCrawlSizeBytes;
var allowedServers = crawlSettings.AllowedServers ?? Array.Empty<string>();
var noIndexPatterns = crawlSettings.NoIndexPatterns ?? Array.Empty<string>();
string logFile = crawlSettings.LogFile;
string statsFile = crawlSettings.StatsFile;
string brokenLinksFile = crawlSettings.BrokenLinksFile;
bool checkExternalLinks = crawlSettings.CheckExternalLinks;
bool noLive = crawlSettings.NoLiveStats;
int requestDelayMs = crawlSettings.RequestDelayMs;
bool feedMode = crawlSettings.Feed;
int crawlWorkers = crawlSettings.CrawlWorkers;

bool showHelp = false;

for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg == "-help" || arg == "--help")
    {
        showHelp = true;
    }
    else if (arg == "--db")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("Error: --db requires a path.");
            return;
        }
        dbPath = args[++i];
    }
    else if (arg == "--max-pages")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[++i], out maxPages) || maxPages <= 0)
        {
            Console.Error.WriteLine("Error: --max-pages requires a positive integer.");
            return;
        }
    }
    else if (arg == "--max-pages-per-host")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[++i], out maxPagesPerHost) || maxPagesPerHost <= 0)
        {
            Console.Error.WriteLine("Error: --max-pages-per-host requires a positive integer.");
            return;
        }
    }
    else if (arg == "--max-crawl-size-bytes")
    {
        if (i + 1 >= args.Length || !long.TryParse(args[++i], out maxCrawlSizeBytes) || maxCrawlSizeBytes <= 0)
        {
            Console.Error.WriteLine("Error: --max-crawl-size-bytes requires a positive long integer.");
            return;
        }
    }
    else if (arg == "--request-delay-ms")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[++i], out requestDelayMs) || requestDelayMs < 0)
        {
            Console.Error.WriteLine("Error: --request-delay-ms requires a non-negative integer.");
            return;
        }
    }
    else if (arg == "--log-file")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("Error: --log-file requires a path.");
            return;
        }
        logFile = args[++i];
    }
    else if (arg == "--stats-file")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("Error: --stats-file requires a path (extension is added automatically).");
            return;
        }
        statsFile = args[++i];
    }
    else if (arg == "--broken-links-file")
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("Error: --broken-links-file requires a path (the .txt extension is added automatically).");
            return;
        }
        brokenLinksFile = args[++i];
    }
    else if (arg == "--check-external-links")
    {
        checkExternalLinks = true;
    }
    else if (arg == "--feed")
    {
        feedMode = true;
    }
    else if (arg == "--crawl-workers")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[++i], out crawlWorkers) || crawlWorkers <= 0)
        {
            Console.Error.WriteLine("Error: --crawl-workers requires a positive integer.");
            return;
        }
    }
    else if (arg == "--no-live")
    {
        noLive = true;
    }
    else if (arg.StartsWith('-'))
    {
        Console.Error.WriteLine($"Error: unknown option '{arg}'. Run with --help for usage.");
        return;
    }
    else if (string.IsNullOrEmpty(url))
    {
        url = arg;
    }
    else
    {
        Console.Error.WriteLine($"Error: unexpected argument '{arg}'. Only one start URL is accepted.");
        return;
    }
}

if (args.Length == 0 || showHelp)
{
    Console.WriteLine("Usage: dotnet run -- [options] <url>");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --db <path>              Path to the SQLite database. Default is 'search.db' in the");
    Console.WriteLine("                           working directory. (Can also be set via 'db' in appsettings.json.)");
    Console.WriteLine("  --max-pages <n>          Maximum number of pages to index this run (304s, skips, and");
    Console.WriteLine("                           failures don't count). Default is infinity. A dev convenience");
    Console.WriteLine("                           for testing against large sites; not meant for normal crawls.");
    Console.WriteLine("                           (Can also be set via 'max-pages' in appsettings.json.)");
    Console.WriteLine("  --max-pages-per-host <n> Stop indexing a host once it has contributed n pages, a guard");
    Console.WriteLine("                           against crawler traps (calendars, faceted nav). Default infinity.");
    Console.WriteLine("                           (Can also be set via 'max-pages-per-host' in appsettings.json.)");
    Console.WriteLine("  --max-crawl-size-bytes <n> Stop downloading/indexing a page/file if its size exceeds");
    Console.WriteLine("                           n bytes. Default is 15728640 (15 MB).");
    Console.WriteLine("                           (Can also be set via 'max-crawl-size-bytes' in appsettings.json.)");
    Console.WriteLine("  --request-delay-ms <n>   Politeness delay in milliseconds between requests to the");
    Console.WriteLine("                           same host. Default is 250 ms. Set to 0 to disable delay.");
    Console.WriteLine("                           (Can also be set via 'request-delay-ms' in appsettings.json.)");
    Console.WriteLine("  --log-file <path>        Path to the run log file. Default is 'crawl.log'. Log messages");
    Console.WriteLine("                           go here, not to the console. (Or 'log-file' in appsettings.json.)");
    Console.WriteLine("  --stats-file <path>      Base path for the end-of-run stats files; '.json' and '.txt'");
    Console.WriteLine("                           are appended. Default is 'crawl-stats'. (Or 'stats-file'.)");
    Console.WriteLine("  --broken-links-file <path>  Base path for the broken-links report ('.txt' is appended).");
    Console.WriteLine("                           Default is 'broken-links'. Lists broken links (errors) and redirected");
    Console.WriteLine("                           links (which still resolve but should be updated), the page each was");
    Console.WriteLine("                           found on, plus unreachable hosts. (Or 'broken-links-file'.)");
    Console.WriteLine("  --check-external-links   After the crawl, probe off-site links (hosts outside the allowed");
    Console.WriteLine("                           set) to confirm they still resolve; broken or redirected ones are");
    Console.WriteLine("                           added to the broken-links report. Off by default. (Or 'check-external-links'.)");
    Console.WriteLine("  --feed                   Treat <url> as an RSS/Atom feed and run an update crawl: only the");
    Console.WriteLine("                           items the feed lists are fetched (unchanged ones answer 304 and");
    Console.WriteLine("                           cost nothing to re-index), links are not followed, and nothing");
    Console.WriteLine("                           the run didn't visit is pruned. Site deletions are reconciled by");
    Console.WriteLine("                           the next full crawl. (Or 'feed' in appsettings.json.)");
    Console.WriteLine("  --crawl-workers <n>      Number of concurrent crawl workers. Default is 4. Each host is");
    Console.WriteLine("                           still fetched sequentially with the politeness delay, so extra");
    Console.WriteLine("                           workers pay off when the crawl spans several hosts.");
    Console.WriteLine("                           (Or 'crawl-workers' in appsettings.json.)");
    Console.WriteLine("  --no-live                Force plain progress lines instead of the live display. Not");
    Console.WriteLine("                           usually needed: the live display turns itself off automatically");
    Console.WriteLine("                           when output is redirected or there is no interactive console");
    Console.WriteLine("                           (e.g. a Windows scheduled task or service).");
    Console.WriteLine("  -help, --help            Show this help message and exit.");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  <url>               The starting URL to crawl. Its exact origin (scheme, host, and");
    Console.WriteLine("                      port — default port if none is given) is always in scope.");
    Console.WriteLine();
    Console.WriteLine("Note: Additional allowed hosts can be configured via the 'allowed-servers' array in");
    Console.WriteLine("appsettings.json. Entries are [scheme://]host[:port]; an omitted scheme or port");
    Console.WriteLine("matches any. The 'www.' variant of the seed host is NOT implied — list it as its");
    Console.WriteLine("own entry to crawl both.");
    Console.WriteLine();
    Console.WriteLine("Pages whose URL matches an entry in the 'noindex-patterns' array in appsettings.json");
    Console.WriteLine("are crawled for their links but never indexed (\"noindex, follow\"). Patterns match the");
    Console.WriteLine("whole URL with '*' as a wildcard and an optional trailing '$' to anchor the end, e.g.");
    Console.WriteLine("'*/tag/*', 'https://example.com/calendar/*', or '*://wiki.example.com/*'.");
    return;
}

if (string.IsNullOrEmpty(url))
{
    Console.WriteLine("Error: Missing required argument <url>.");
    Console.WriteLine("Usage: dotnet run -- [options] <url>");
    Console.WriteLine("Run with --help for more information.");
    return;
}

string fullDbPath = Path.GetFullPath(dbPath);
var dbDirectory = Path.GetDirectoryName(fullDbPath);
if (!string.IsNullOrEmpty(dbDirectory))
{
    Directory.CreateDirectory(dbDirectory);
}
string connectionString = $"Data Source={fullDbPath}";

string logPath = Path.GetFullPath(logFile);
string statsJsonPath = Path.GetFullPath(statsFile + ".json");
string statsTextPath = Path.GetFullPath(statsFile + ".txt");
string brokenLinksPath = Path.GetFullPath(brokenLinksFile + ".txt");

// Channel 2: log messages go to a file (not the console — the live display owns that). Quiet the
// per-request HttpClient chatter so the crawler's own messages stay readable.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .WriteTo.File(logPath, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var services = new ServiceCollection();
    services.AddLogging(b =>
    {
        b.ClearProviders();
        b.AddSerilog(Log.Logger, dispose: false);
    });

    // AddHttpClient<CrawlerService> registers the typed client AND CrawlerService itself,
    // so the configured User-Agent and retry policy are actually applied. (Do not also
    // register CrawlerService separately, or that registration would shadow this one.)
    services.AddHttpClient<CrawlerService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", CrawlerService.UserAgent);
    })
    // Advertise and transparently decompress gzip/deflate/brotli, so most pages transfer at a
    // fraction of their raw size instead of uncompressed.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

    // Local embeddings (snowflake-arctic-embed-s, 384-dim, CPU/ONNX); the model is downloaded
    // at build time and bundled next to the binaries — see Directory.Build.props.
    services.AddSingleton<IEmbedder>(_ => new LocalEmbedderAdapter());
    services.AddSingleton(new DatabaseConfig(connectionString));
    services.AddSqliteVectorStore(_ => connectionString);
    services.AddSingleton<VectorSearchService>();

    var serviceProvider = services.BuildServiceProvider();

    // Initialize the schema (vector store first, then the crawler's FTS/triggers).
    var vectorService = serviceProvider.GetRequiredService<VectorSearchService>();
    await vectorService.EnsureCreatedAsync();

    var crawlerService = serviceProvider.GetRequiredService<CrawlerService>();
    await crawlerService.EnsureCreatedAsync();

    // CTRL+C is left to its default: it terminates the process. The crawler no longer tries to stop
    // gracefully — every page's database writes are applied in a single transaction, so a hard kill
    // mid-crawl can't leave a torn write, and a stalled fetch is bounded by its own request timeout.

    // Banner, printed before the live display takes over the console. The database and log paths
    // lead, so a user (or someone inspecting a scheduled run) sees where output is being written
    // before anything else scrolls past.
    AnsiConsole.Write(new Rule("[bold]LocalSearchEngine crawler[/]").LeftJustified());
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLineInterpolated($"[grey]Database[/]  {fullDbPath}");
    AnsiConsole.MarkupLineInterpolated($"[grey]{(feedMode ? "Feed" : "Seed")}[/]      {url}");
    AnsiConsole.MarkupLineInterpolated($"[grey]Log[/]       {logPath}");
    AnsiConsole.MarkupLineInterpolated($"[grey]Stats[/]     {statsJsonPath}");
    AnsiConsole.MarkupLineInterpolated($"[grey]Broken[/]    {brokenLinksPath}");
    AnsiConsole.MarkupLineInterpolated($"[grey]Request delay[/] {requestDelayMs} ms");
    if (maxPages != int.MaxValue)
    {
        AnsiConsole.MarkupLineInterpolated($"[grey]Max pages[/] {maxPages}");
    }
    if (maxPagesPerHost != int.MaxValue)
    {
        AnsiConsole.MarkupLineInterpolated($"[grey]Per-host cap[/] {maxPagesPerHost}");
    }
    if (allowedServers.Length > 0)
    {
        AnsiConsole.MarkupLineInterpolated($"[grey]Allowed servers[/] {string.Join(", ", allowedServers)}");
    }
    if (noIndexPatterns.Length > 0)
    {
        AnsiConsole.MarkupLineInterpolated($"[grey]Noindex patterns[/] {string.Join(", ", noIndexPatterns)}");
    }
    AnsiConsole.WriteLine();

    // The live display runs only on an interactive console. --no-live forces plain in a terminal that
    // would otherwise qualify.
    bool useLive = !noLive && AnsiConsole.Profile.Capabilities.Interactive;

    // The one place run composition is chosen: a full crawl (sitemaps + seed, follow links, prune)
    // or a feed-driven update (fetch exactly what the feed lists, delete nothing else).
    Task<CrawlReport> RunCrawlAsync(ICrawlReporter reporter) => feedMode
        ? crawlerService.CrawlFeedAsync(url, maxPages, allowedServers, noIndexPatterns, maxCrawlSizeBytes, reporter, requestDelayMs, crawlWorkers)
        : crawlerService.CrawlAsync(url, maxPages, allowedServers, noIndexPatterns, maxPagesPerHost, maxCrawlSizeBytes, checkExternalLinks, reporter, requestDelayMs, crawlWorkers);

    CrawlReport report;
    if (useLive)
    {
        CrawlReport? captured = null;
        await AnsiConsole.Live(new Markup("[grey]Starting…[/]"))
            .AutoClear(false) // leave the final frame on screen
            .StartAsync(async live =>
            {
                captured = await RunCrawlAsync(new SpectreCrawlReporter(live));
            });
        report = captured!;
    }
    else
    {
        report = await RunCrawlAsync(new PlainCrawlReporter(AnsiConsole.Console));
    }

    // Channel 3: write the end-of-run stats to disk (JSON + text), then print a summary.
    await CrawlStatsWriter.WriteAsync(report, statsJsonPath, statsTextPath, CancellationToken.None);
    await BrokenLinksWriter.WriteAsync(report, brokenLinksPath, checkExternalLinks, CancellationToken.None);
    AnsiConsole.WriteLine();
    AnsiConsole.Write(SummaryPanel.Build(report, statsJsonPath, statsTextPath, logPath, brokenLinksPath));
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// The crawler's <c>CrawlSettings</c> section from appsettings.json — a plain options POCO bound by
/// Microsoft.Extensions.Configuration. Each kebab-case JSON key is mapped to its property with
/// <see cref="ConfigurationKeyNameAttribute"/>: without it the binder matches only PascalCase property
/// names, so keys like <c>allowed-servers</c> would silently leave every value at its default (which is
/// how a crawl once ended up limited to just its seed host). Property defaults below are the values used
/// when a key is absent or null.
/// </summary>
internal sealed class CrawlSettings
{
    /// <summary>Maximum pages to index this run; <c>null</c> (the JSON "no limit" convention) means unbounded.</summary>
    [ConfigurationKeyName("max-pages")]
    public int? MaxPages { get; set; }

    /// <summary>Maximum pages to index per host; <c>null</c> (the JSON "no limit" convention) means unbounded.</summary>
    [ConfigurationKeyName("max-pages-per-host")]
    public int? MaxPagesPerHost { get; set; }

    /// <summary>Maximum size in bytes of any single downloaded page/file. Defaults to 15 MB.</summary>
    [ConfigurationKeyName("max-crawl-size-bytes")]
    public long MaxCrawlSizeBytes { get; set; } = 15 * 1024 * 1024;

    /// <summary>Politeness delay in milliseconds between requests to the same host. Defaults to 250.</summary>
    [ConfigurationKeyName("request-delay-ms")]
    public int RequestDelayMs { get; set; } = 250;

    /// <summary>Additional allowed hosts as [scheme://]host[:port]; null/empty means the seed origin only.</summary>
    [ConfigurationKeyName("allowed-servers")]
    public string[]? AllowedServers { get; set; }

    /// <summary>URL glob patterns whose pages are followed for links but never indexed ("noindex, follow").</summary>
    [ConfigurationKeyName("noindex-patterns")]
    public string[]? NoIndexPatterns { get; set; }

    /// <summary>Whether to probe off-site links after the crawl to confirm they still resolve.</summary>
    [ConfigurationKeyName("check-external-links")]
    public bool CheckExternalLinks { get; set; }

    /// <summary>Whether the URL is an RSS/Atom feed driving an update crawl instead of a full crawl.</summary>
    [ConfigurationKeyName("feed")]
    public bool Feed { get; set; }

    /// <summary>Number of concurrent crawl workers. Defaults to 4; each host still fetches sequentially.</summary>
    [ConfigurationKeyName("crawl-workers")]
    public int CrawlWorkers { get; set; } = 4;

    /// <summary>Whether to force plain progress lines instead of the live display.</summary>
    [ConfigurationKeyName("no-live-stats")]
    public bool NoLiveStats { get; set; }

    /// <summary>Path to the run log file.</summary>
    [ConfigurationKeyName("log-file")]
    public string LogFile { get; set; } = "crawl.log";

    /// <summary>Base path for the end-of-run stats files ('.json' and '.txt' are appended).</summary>
    [ConfigurationKeyName("stats-file")]
    public string StatsFile { get; set; } = "crawl-stats.log";

    /// <summary>Base path for the broken-links report ('.txt' is appended).</summary>
    [ConfigurationKeyName("broken-links-file")]
    public string BrokenLinksFile { get; set; } = "crawl-broken-links.log";
}