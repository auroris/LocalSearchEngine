using LocalSearchEngine.Core;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.TextProcessing;
using Microsoft.SemanticKernel;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.Configure<SearchSettings>(builder.Configuration.GetSection("SearchSettings"));

// Local embeddings run on the CPU via ONNX Runtime. The model (snowflake-arctic-embed-s,
// 384-dim) is fetched at build time and copied next to the app; see Directory.Build.props.
builder.Services.AddSingleton<IEmbedder>(_ => new LocalEmbedderAdapter());

// The web app only issues SELECTs against the index the crawler builds, but it opens
// the connection ReadWrite (not ReadOnly) on purpose: a WAL reader needs write access
// to the -shm wal-index to read a database the crawler is actively writing.
//
// The DB path comes from the "db" setting (default "search.db"), resolved relative to the
// app's content root so the index sits alongside the app's own files; point the crawler at the
// same file with its --db option. A full ConnectionStrings:SearchDb still wins when present,
// for setups that need to tune the connection string (e.g. a different open mode) directly.
string? configuredDb = builder.Configuration["db"];
string dbPath = string.IsNullOrWhiteSpace(configuredDb) ? "search.db" : configuredDb;
if (!Path.IsPathRooted(dbPath))
{
    dbPath = Path.Combine(builder.Environment.ContentRootPath, dbPath);
}

string connectionString = builder.Configuration.GetConnectionString("SearchDb")
    ?? $"Data Source={dbPath};Mode=ReadWrite";

builder.Services.AddSingleton(new DatabaseConfig(connectionString));
builder.Services.AddSqliteVectorStore(_ => connectionString);
builder.Services.AddSingleton<VectorSearchService>();

var app = builder.Build();

// Surface a clear message if the crawler hasn't produced an index yet, instead of
// failing obscurely on the first query.
var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
if (!string.IsNullOrEmpty(dataSource) && !File.Exists(dataSource))
{
    app.Logger.LogWarning(
        "Search database '{Path}' was not found. Build it first with: dotnet run --project LocalSearchEngine.Crawler -- <url>",
        Path.GetFullPath(dataSource));
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred. Please try again later." });
        });
    });
}

app.UseHttpsRedirection();

// Serve the frontend
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.Run();
