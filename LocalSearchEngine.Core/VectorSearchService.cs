using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

namespace LocalSearchEngine.Core;

public class TextChunkRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [VectorStoreData]
    public string Url { get; set; } = string.Empty;

    [VectorStoreData]
    public string Text { get; set; } = string.Empty;

    [VectorStoreData]
    public bool IsHeading { get; set; } = false;

    [VectorStoreVector(384)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

public class VectorSearchService
{
    private readonly IEmbedder _embedder;
    private readonly VectorStoreCollection<string, TextChunkRecord> _collection;
    private readonly DatabaseConfig _dbConfig;
    private readonly SearchSettings _settings;
    private readonly ILogger<VectorSearchService> _logger;

    public VectorSearchService(IEmbedder embedder, VectorStore vectorStore, DatabaseConfig dbConfig, IOptions<SearchSettings> options, ILogger<VectorSearchService> logger)
    {
        _embedder = embedder;
        _collection = vectorStore.GetCollection<string, TextChunkRecord>("text_chunks");
        _dbConfig = dbConfig;
        _settings = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Creates the vector collection (and its backing tables) if missing and enables
    /// WAL. Writers (the crawler) call this; the read-only web app does not.
    /// </summary>
    public async Task EnsureCreatedAsync()
    {
        await _collection.EnsureCollectionExistsAsync();
        using var connection = new SqliteConnection(_dbConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteUrlChunksAsync(string url)
    {
        try
        {
            // Collect this URL's chunk ids, then delete them through the collection so
            // the connector removes both the data row and its companion vector row.
            // The AFTER DELETE trigger on text_chunks keeps the FTS index in sync.
            var ids = new List<string>();
            using (var connection = new SqliteConnection(_dbConfig.ConnectionString))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id FROM text_chunks WHERE Url = @Url";
                command.Parameters.AddWithValue("@Url", url);
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    ids.Add(reader.GetString(0));
                }
            }

            if (ids.Count > 0)
            {
                await _collection.DeleteAsync(ids);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete URL chunks for {Url}", url);
            throw;
        }
    }

    public async Task IndexUrlChunksAsync(string url, string fullText, bool isHeading = false)
    {
        foreach (var chunkText in TextChunker.Chunk(fullText))
        {
            var record = new TextChunkRecord
            {
                Id = Guid.NewGuid().ToString(),
                Url = url,
                Text = chunkText,
                IsHeading = isHeading,
                Embedding = _embedder.Embed(chunkText) // generated locally on the CPU
            };

            await _collection.UpsertAsync(record);
        }
    }

    /// <summary>
    /// Hybrid search returning every "close enough" result (vector hits at or above
    /// the similarity threshold, plus all exact keyword matches) — no count cap.
    /// Ranking and thresholding live in <see cref="SearchRanker"/>.
    /// </summary>
    public async Task<SearchResponse> SearchAsync(string query)
    {
        int pool = _settings.CandidatePoolSize;

        // Semantic candidates: the connector returns nearest-first; Score is cosine distance.
        var vectorHits = new List<VectorCandidate>();
        var queryEmbedding = _embedder.Embed(query);
        await foreach (var result in _collection.SearchAsync(queryEmbedding, pool))
        {
            vectorHits.Add(new VectorCandidate(
                result.Record.Url,
                result.Record.Text,
                result.Record.IsHeading,
                result.Score ?? double.MaxValue));
        }

        // Exact keyword candidates: phrase match against the FTS5 index.
        var keywordHits = new List<KeywordCandidate>();
        try
        {
            using var connection = new SqliteConnection(_dbConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();

            string escapedQuery = query.Replace("\"", "\"\"");
            command.CommandText = @"
                SELECT c.Url, c.Text, c.IsHeading
                FROM text_chunks_fts f
                JOIN text_chunks c ON f.Id = c.Id
                WHERE text_chunks_fts MATCH @query
                ORDER BY f.rank
                LIMIT @limit";
            command.Parameters.AddWithValue("@query", $"\"{escapedQuery}\"");
            command.Parameters.AddWithValue("@limit", pool);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                bool isHeading = !reader.IsDBNull(2) && reader.GetBoolean(2);
                keywordHits.Add(new KeywordCandidate(reader.GetString(0), reader.GetString(1), isHeading));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch exact matches from SQLite FTS5 for query {Query}", query);
        }

        var items = SearchRanker.Rank(vectorHits, keywordHits, query, _settings);
        return new SearchResponse { Items = items, TotalMatches = items.Count };
    }
}
