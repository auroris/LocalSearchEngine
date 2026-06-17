// <summary>
// Main entry point for the LocalSearchEngine.Crawler application.
// This application provides a command-line interface for crawling websites,
// extracting content, generating embeddings, and storing the results
// in a SQLite vector database for the search engine to use.
// </summary>

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Reporting;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.TextProcessing;
using LocalSearchEngine.Crawler;
using Polly;
using Polly.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using System.Net;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .Build();

string url = "";
string dbPath = !string.IsNullOrWhiteSpace(config["db"]) ? config["db"]! : "search.db";
int maxPages = config.GetValue<int?>("max-pages") ?? int.MaxValue;
int maxPagesPerHost = config.GetValue<int?>("max-pages-per-host") ?? int.MaxValue;
long maxCrawlSizeBytes = config.GetValue<long?>("max-crawl-size-bytes") ?? 15 * 1024 * 1024;
var allowedServers = config.GetSection("allowed-servers").Get<string[]>() ?? Array.Empty<string>();
string logFile = !string.IsNullOrWhiteSpace(config["log-file"]) ? config["log-file"]! : "crawl.log";
string statsFile = !string.IsNullOrWhiteSpace(config["stats-file"]) ? config["stats-file"]! : "crawl-stats";
string brokenLinksFile = !string.IsNullOrWhiteSpace(config["broken-links-file"]) ? config["broken-links-file"]! : "broken-links";
bool checkExternalLinks = config.GetValue<bool?>("check-external-links") ?? false;
bool noLive = config.GetValue<bool?>("no-live") ?? false;

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

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true; // let the crawler stop gracefully and flush
        cts.Cancel();
    };

    // Banner, printed before the live display takes over the console. The database and log paths
    // lead, so a user (or someone inspecting a scheduled run) sees where output is being written
    // before anything else scrolls past.
    AnsiConsole.Write(new Rule("[bold]LocalSearchEngine crawler[/]").LeftJustified());
    AnsiConsole.MarkupLineInterpolated($"[grey]Database[/]  {fullDbPath}");
    AnsiConsole.MarkupLineInterpolated($"[grey]Seed[/]      {url}");
    AnsiConsole.MarkupLineInterpolated($"[grey]Log[/]       {logPath}");
    AnsiConsole.MarkupLineInterpolated($"[grey]Stats[/]     {statsJsonPath}");
    AnsiConsole.MarkupLineInterpolated($"[grey]Broken[/]    {brokenLinksPath}");
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
    AnsiConsole.WriteLine();

    // The live display runs only on an interactive console. --no-live forces plain in a terminal that
    // would otherwise qualify.
    bool useLive = !noLive && AnsiConsole.Profile.Capabilities.Interactive;

    CrawlReport report;
    if (useLive)
    {
        CrawlReport? captured = null;
        await AnsiConsole.Live(new Markup("[grey]Starting…[/]"))
            .AutoClear(false) // leave the final frame on screen
            .StartAsync(async live =>
            {
                var reporter = new SpectreCrawlReporter(live);
                captured = await crawlerService.CrawlAsync(url, maxPages, allowedServers, maxPagesPerHost, maxCrawlSizeBytes, checkExternalLinks, reporter, cts.Token);
            });
        report = captured!;
    }
    else
    {
        var reporter = new PlainCrawlReporter(AnsiConsole.Console);
        report = await crawlerService.CrawlAsync(url, maxPages, allowedServers, maxPagesPerHost, maxCrawlSizeBytes, checkExternalLinks, reporter, cts.Token);
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
