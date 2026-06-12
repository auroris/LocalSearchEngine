using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using LocalSearchEngine.Core;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.TextProcessing;
using Polly;
using Polly.Extensions.Http;
using Microsoft.Extensions.Configuration;
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
    Console.WriteLine("                           failures don't count). Default is infinity.");
    Console.WriteLine("                           (Can also be set via 'max-pages' in appsettings.json.)");
    Console.WriteLine("  --max-pages-per-host <n> Stop indexing a host once it has contributed n pages, a guard");
    Console.WriteLine("                           against crawler traps (calendars, faceted nav). Default infinity.");
    Console.WriteLine("                           (Can also be set via 'max-pages-per-host' in appsettings.json.)");
    Console.WriteLine("  --max-crawl-size-bytes <n> Stop downloading/indexing a page/file if its size exceeds");
    Console.WriteLine("                           n bytes. Default is 15728640 (15 MB).");
    Console.WriteLine("                           (Can also be set via 'max-crawl-size-bytes' in appsettings.json.)");
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

var services = new ServiceCollection();

// Add Logging
services.AddLogging(configure => configure.AddSimpleConsole(options =>
{
    options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
}));

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
    Console.WriteLine("Cancellation requested; finishing the current page...");
    cts.Cancel();
};

Console.WriteLine($"Starting spider for: {url} with max pages: {(maxPages == int.MaxValue ? "infinity" : maxPages)}");
if (maxPagesPerHost != int.MaxValue)
{
    Console.WriteLine($"Per-host page cap: {maxPagesPerHost}");
}
Console.WriteLine($"Max crawl size per page/file: {maxCrawlSizeBytes} bytes");
if (allowedServers.Length > 0)
{
    Console.WriteLine($"Allowed additional servers: {string.Join(", ", allowedServers)}");
}
Console.WriteLine($"Database: {fullDbPath}");
await crawlerService.CrawlAsync(url, maxPages, allowedServers, maxPagesPerHost, maxCrawlSizeBytes, cts.Token);
Console.WriteLine("Spider completed.");
