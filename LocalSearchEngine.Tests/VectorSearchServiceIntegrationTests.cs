using LocalSearchEngine.Core;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Searching;
using LocalSearchEngine.Core.TextProcessing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Xunit;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Exercises the real sqlite-vec connector end to end against a temporary database,
/// using a deterministic fake embedder so no ONNX model download is needed. These
/// tests are what verify the two things unit tests can't: that a self-match ranks #1
/// (cosine distance direction) and that deleting a URL clears the data, vector, and
/// FTS tables together (the orphaned-embeddings fix).
/// </summary>
public sealed class VectorSearchServiceIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ServiceProvider _provider;
    private readonly VectorSearchService _service;

    public VectorSearchServiceIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lse_itest_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";

        var services = new ServiceCollection();
        services.AddSqliteVectorStore(_ => _connectionString);
        _provider = services.BuildServiceProvider();

        var store = _provider.GetRequiredService<VectorStore>();
        var settings = Options.Create(new SearchSettings { MaxDistance = 1.0, CandidatePoolSize = 100 });
        _service = new VectorSearchService(
            new FakeEmbedder(), store, new DatabaseConfig(_connectionString), settings, NullLogger<VectorSearchService>.Instance);
    }

    private async Task SeedSchemaAndDataAsync()
    {
        // Real schema setup: vector collection first, then the crawler's FTS + triggers.
        await _service.EnsureCreatedAsync();
        var crawler = new CrawlerService(new HttpClient(), _service, NullLogger<CrawlerService>.Instance, new DatabaseConfig(_connectionString));
        await crawler.EnsureCreatedAsync();

        await _service.IndexUrlChunksAsync("https://site/alpha", "The Wizard's Grace session recap notes");
        await _service.IndexUrlChunksAsync("https://site/beta", "Citadel Altaerein battlements gambit encounter");
        await _service.IndexUrlChunksAsync("https://site/gamma", "Mwangi Expanse thornscales arrival journey");
    }

    [Fact]
    public async Task Self_match_ranks_first_with_near_zero_distance()
    {
        await SeedSchemaAndDataAsync();

        // Querying with a chunk's exact text reproduces its embedding, so cosine
        // distance is ~0 -> similarity ~1, and that document must rank first.
        var response = await _service.SearchAsync("Citadel Altaerein battlements gambit encounter");

        Assert.NotEmpty(response.Items);
        Assert.Equal("https://site/beta", response.Items[0].Url);
        Assert.True(response.Items[0].Similarity > 0.99,
            $"expected self-match similarity ~1.0 but got {response.Items[0].Similarity}");
    }

    [Fact]
    public async Task Deleting_a_url_clears_data_vector_and_fts_rows_together()
    {
        await SeedSchemaAndDataAsync();

        long dataBefore = Count("SELECT COUNT(*) FROM text_chunks");
        long vecBefore = Count("SELECT COUNT(*) FROM vec_text_chunks_rowids");
        long ftsBefore = Count("SELECT COUNT(*) FROM text_chunks_fts");

        // Every data row should have exactly one vector row and one FTS row.
        Assert.Equal(dataBefore, vecBefore);
        Assert.Equal(dataBefore, ftsBefore);

        long betaRows = Count("SELECT COUNT(*) FROM text_chunks WHERE Url = 'https://site/beta'");
        Assert.True(betaRows > 0);

        await _service.DeleteUrlChunksAsync("https://site/beta");

        long dataAfter = Count("SELECT COUNT(*) FROM text_chunks");
        long vecAfter = Count("SELECT COUNT(*) FROM vec_text_chunks_rowids");
        long ftsAfter = Count("SELECT COUNT(*) FROM text_chunks_fts");

        Assert.Equal(dataBefore - betaRows, dataAfter);
        Assert.Equal(dataAfter, vecAfter); // no orphaned embeddings left behind
        Assert.Equal(dataAfter, ftsAfter); // FTS stayed consistent via the trigger
        Assert.Equal(0, Count("SELECT COUNT(*) FROM text_chunks WHERE Url = 'https://site/beta'"));
    }

    private long Count(string sql)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public void Dispose()
    {
        _provider.Dispose();
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

    [Fact]
    public void TestReflection()
    {
        var type = typeof(SmartComponents.LocalEmbeddings.LocalEmbedder);
        var sb = new System.Text.StringBuilder();
        foreach (var ctor in type.GetConstructors())
        {
            sb.Append("Constructor: ").Append(ctor.Name).Append("(");
            foreach (var p in ctor.GetParameters())
            {
                sb.Append(p.ParameterType.FullName).Append(" ").Append(p.Name).Append(", ");
            }
            sb.AppendLine(")");
        }
        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
        {
            sb.AppendLine($"Field: {field.FieldType.FullName} {field.Name}");
        }
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
        {
            sb.AppendLine($"Property: {prop.PropertyType.FullName} {prop.Name}");
        }
        throw new Exception(sb.ToString());
    }

    /// <summary>Deterministic, model-free embedder: identical text yields an identical vector.</summary>
    private sealed class FakeEmbedder : IEmbedder
    {
        // No asymmetric query instruction in the fake, so a query equal to a chunk's text
        // reproduces that chunk's vector exactly (the self-match test depends on this).
        public ReadOnlyMemory<float> EmbedQuery(string text) => Embed(text);

        public ReadOnlyMemory<float> Embed(string text)
        {
            // FNV-1a hash -> LCG fill, so the result is stable across runs and processes.
            ulong hash = 1469598103934665603UL;
            foreach (char ch in text)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }

            var vector = new float[384];
            ulong state = hash | 1UL;
            double sumSq = 0;
            for (int i = 0; i < vector.Length; i++)
            {
                state = state * 6364136223846793005UL + 1442695040888963407UL;
                double value = ((state >> 33) / (double)(1UL << 31)) - 1.0; // ~[-1, 1]
                vector[i] = (float)value;
                sumSq += value * value;
            }

            double norm = Math.Sqrt(sumSq);
            if (norm > 0)
            {
                for (int i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
            }
            return vector;
        }
    }
}
