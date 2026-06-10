using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartComponents.LocalEmbeddings;
using Microsoft.SemanticKernel;
using LocalSearchEngine.Core;

string url = "";
string dbPath = "search.db";
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
    Console.WriteLine("Usage: dotnet run -- [--db <path>] <url>");
    return;
}

string connectionString = $"Data Source={dbPath};Cache=Shared";

var services = new ServiceCollection();

// Add Logging
services.AddLogging(configure => configure.AddConsole());

// Add Services
services.AddHttpClient<CrawlerService>();
services.AddSingleton<LocalEmbedder>();
services.AddSingleton(new DatabaseConfig(connectionString));
services.AddSqliteVectorStore(_ => connectionString);
services.AddSingleton<VectorSearchService>();
services.AddScoped<CrawlerService>();

var serviceProvider = services.BuildServiceProvider();

// Initialize
var vectorService = serviceProvider.GetRequiredService<VectorSearchService>();
await vectorService.InitializeAsync();

var crawlerService = serviceProvider.GetRequiredService<CrawlerService>();
await crawlerService.InitializeAsync();

Console.WriteLine($"Starting spider for: {url} with max pages: {(maxPages == int.MaxValue ? "infinity" : maxPages)}");
await crawlerService.CrawlAsync(url, maxPages);
Console.WriteLine("Spider completed.");
