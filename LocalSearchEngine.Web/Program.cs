using SmartComponents.LocalEmbeddings;
using LocalSearchEngine.Core;
using Microsoft.SemanticKernel;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.Configure<SearchSettings>(builder.Configuration.GetSection("SearchSettings"));

// Register Local Embeddings (this downloads the model on first run if needed)
// It defaults to all-MiniLM-L6-v2 which is highly efficient.
builder.Services.AddSingleton<LocalEmbedder>();

// Register Sqlite Vector Store
string connectionString = "Data Source=search.db;Mode=ReadOnly";
builder.Services.AddSingleton(new DatabaseConfig(connectionString));
builder.Services.AddSqliteVectorStore(_ => connectionString);

// Register our custom simple in-memory vector store as a singleton
builder.Services.AddSingleton<VectorSearchService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var vectorService = scope.ServiceProvider.GetRequiredService<VectorSearchService>();
    vectorService.InitializeAsync().GetAwaiter().GetResult();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    // Development specific config
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
