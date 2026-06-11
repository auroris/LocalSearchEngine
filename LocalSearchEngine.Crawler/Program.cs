using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using LocalSearchEngine.Core;
using Polly;
using Polly.Extensions.Http;

string url = "";
string dbPath = DefaultDbPath();
int maxPages = int.MaxValue;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--db" && i + 1 < args.Length)
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

if (string.IsNullOrEmpty(url))
{
    Console.WriteLine("Usage: dotnet run -- [--db <path>] [--max-pages <n>] <url>");
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
Console.WriteLine($"Database: {fullDbPath}");
await crawlerService.CrawlAsync(url, maxPages, cts.Token);
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
