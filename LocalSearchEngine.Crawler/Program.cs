using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using LocalSearchEngine.Core;
using Polly;
using Polly.Extensions.Http;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .Build();

string url = "";
string dbPath = config["db"] ?? DefaultDbPath();
int maxPages = config.GetValue<int?>("max-pages") ?? int.MaxValue;
var allowedServers = config.GetSection("allowed-servers").Get<string[]>() ?? Array.Empty<string>();

bool showHelp = false;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "-help" || args[i] == "--help")
    {
        showHelp = true;
    }
    else if (args[i] == "--db" && i + 1 < args.Length)
    {
        dbPath = args[++i];
    }
    else if (args[i] == "--max-pages" && i + 1 < args.Length)
    {
        if (int.TryParse(args[++i], out int parsedMax))
        {
            maxPages = parsedMax;
        }
    }
    else if (string.IsNullOrEmpty(url))
    {
        url = args[i];
    }
}

if (args.Length == 0 || showHelp)
{
    Console.WriteLine("Usage: dotnet run -- [options] <url>");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --db <path>         Path to the SQLite database. Default is search.db or adjacent web project's DB.");
    Console.WriteLine("                      (Can also be set via 'db' in appsettings.json)");
    Console.WriteLine("  --max-pages <n>     Maximum number of pages to crawl. Default is infinity.");
    Console.WriteLine("                      (Can also be set via 'max-pages' in appsettings.json)");
    Console.WriteLine("  -help, --help       Show this help message and exit.");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  <url>               The starting URL to crawl.");
    Console.WriteLine();
    Console.WriteLine("Note: Additional allowed domains can be configured via the 'allowed-servers' array in appsettings.json.");
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
string connectionString = $"Data Source={fullDbPath};Cache=Shared";

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
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

// Local embeddings (bge-small-en-v1.5, CPU/ONNX); model bundled at build time.
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
if (allowedServers.Length > 0)
{
    Console.WriteLine($"Allowed additional servers: {string.Join(", ", allowedServers)}");
}
Console.WriteLine($"Database: {fullDbPath}");
await crawlerService.CrawlAsync(url, maxPages, allowedServers, cts.Token);
Console.WriteLine("Spider completed.");

// By default, write the index into the web app's files so it can serve it without
// any extra configuration. In the repo/dev layout the web project sits next to this
// one; when published standalone (no sibling), fall back to the current directory.
// Override anytime with --db <path>.
static string DefaultDbPath()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var webDir = Path.Combine(dir.FullName, "LocalSearchEngine.Web");
        if (Directory.Exists(webDir))
        {
            return Path.Combine(webDir, "search.db");
        }
    }
    return "search.db";
}
