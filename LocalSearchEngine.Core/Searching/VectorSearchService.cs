using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

using LocalSearchEngine.Core.TextProcessing;

namespace LocalSearchEngine.Core.Searching;

/// <summary>
/// Represents a structured text chunk and its generated vector embedding stored in the index database.
/// </summary>
public class TextChunkRecord
{
    /// <summary>Gets or sets the unique identifier of the chunk record.</summary>
    [VectorStoreKey]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Gets or sets the URL of the page containing the chunk.</summary>
    [VectorStoreData]
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the text content of the chunk.</summary>
    [VectorStoreData]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the chunk represents a heading.</summary>
    [VectorStoreData]
    public bool IsHeading { get; set; } = false;

    /// <summary>Gets or sets the 384-dimensional vector embedding of the chunk text.</summary>
    [VectorStoreVector(384)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>
/// Provides vector search and keyword hybrid query operations over the text chunks index in SQLite.
/// </summary>
public class VectorSearchService
{
    private readonly IEmbedder _embedder;
    private readonly VectorStoreCollection<string, TextChunkRecord> _collection;
    private readonly DatabaseConfig _dbConfig;
    private readonly SearchSettings _settings;
    private readonly ILogger<VectorSearchService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VectorSearchService"/> class.
    /// </summary>
    /// <param name="embedder">The local embedder used to generate text embeddings.</param>
    /// <param name="vectorStore">The vector database store provider.</param>
    /// <param name="dbConfig">The configuration containing database connection details.</param>
    /// <param name="options">The relevance scoring settings options.</param>
    /// <param name="logger">The logger instance.</param>
    public VectorSearchService(IEmbedder embedder, VectorStore vectorStore, DatabaseConfig dbConfig, IOptions<SearchSettings> options, ILogger<VectorSearchService> logger)
    {
        _embedder = embedder;
        _collection = vectorStore.GetCollection<string, TextChunkRecord>("text_chunks");
        _dbConfig = dbConfig;
        _settings = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Ensures that the vector search collection and backing database tables exist.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task EnsureCreatedAsync()
    {
        await _collection.EnsureCollectionExistsAsync();
        using var connection = new SqliteConnection(_dbConfig.ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes all indexed text chunks and their companion vector rows associated with the specified URL.
    /// </summary>
    /// <param name="url">The URL whose chunks to delete.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
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

    /// <summary>
    /// Splits page text into chunks, embeds each, and inserts them into the vector database.
    /// </summary>
    /// <param name="url">The source page URL.</param>
    /// <param name="fullText">The text content to be chunked and indexed.</param>
    /// <param name="isHeading"><c>true</c> if the text consists of page headings; otherwise, <c>false</c>.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task IndexUrlChunksAsync(string url, string fullText, bool isHeading = false)
    {
        var records = new List<TextChunkRecord>();
        foreach (var chunkText in TextChunker.Chunk(fullText))
        {
            records.Add(new TextChunkRecord
            {
                Id = Guid.NewGuid().ToString(),
                Url = url,
                Text = chunkText,
                IsHeading = isHeading,
                Embedding = _embedder.Embed(chunkText) // generated locally on the CPU
            });
        }

        // One batched upsert (a single transaction) per page instead of one per chunk.
        if (records.Count > 0)
        {
            await _collection.UpsertAsync(records);
        }
    }

    /// <summary>
    /// Performs a hybrid search (semantic vector + FTS5 keyword) for the specified query and ranks the results.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <returns>A <see cref="SearchResponse"/> object containing the ranked results.</returns>
    public async Task<SearchResponse> SearchAsync(string query)
    {
        int pool = _settings.CandidatePoolSize;

        // Vector search (embed + ANN scan) and keyword search (FTS5) are independent, so kick
        // the keyword query off first and let it overlap the embedding + vector scan.
        var keywordTask = GetKeywordHitsAsync(query, pool);
        var vectorHits = await GetVectorHitsAsync(query, pool);
        var keywordHits = await keywordTask;

        // Titles for every candidate URL feed the title boost and the result list.
        var titles = await LoadTitlesAsync(vectorHits, keywordHits);

        var items = SearchRanker.Rank(vectorHits, keywordHits, query, _settings, titles);
        return new SearchResponse { Items = items, TotalMatches = items.Count };
    }

    /// <summary>
    /// Retrieves nearest semantic candidate hits from the vector database.
    /// </summary>
    /// <param name="query">The query string.</param>
    /// <param name="pool">The candidate pool size to retrieve.</param>
    /// <returns>A list of <see cref="VectorCandidate"/> matches.</returns>
    private async Task<List<VectorCandidate>> GetVectorHitsAsync(string query, int pool)
    {
        var vectorHits = new List<VectorCandidate>();
        var queryEmbedding = _embedder.EmbedQuery(query);
        await foreach (var result in _collection.SearchAsync(queryEmbedding, pool))
        {
            vectorHits.Add(new VectorCandidate(
                result.Record.Url,
                result.Record.Text,
                result.Record.IsHeading,
                result.Score ?? double.MaxValue));
        }
        return vectorHits;
    }

    /// <summary>
    /// Retrieves keyword candidates matching the query from the full-text search index.
    /// </summary>
    /// <param name="query">The query string.</param>
    /// <param name="pool">The candidate pool limit size.</param>
    /// <returns>A list of <see cref="KeywordCandidate"/> matches.</returns>
    private async Task<List<KeywordCandidate>> GetKeywordHitsAsync(string query, int pool)
    {
        var keywordHits = new List<KeywordCandidate>();
        try
        {
            using var connection = new SqliteConnection(_dbConfig.ConnectionString);
            await connection.OpenAsync();

            // Tier 1: verbatim phrase match.
            await CollectKeywordHitsAsync(connection, $"\"{query.Replace("\"", "\"\"")}\"", exactPhrase: true, pool, keywordHits);

            // Tier 2: looser all-terms (AND) match. Skipped for single-term queries, where
            // it would just duplicate the phrase tier.
            var terms = Regex.Matches(query, @"\w+").Select(m => m.Value).ToList();
            if (terms.Count > 1)
            {
                await CollectKeywordHitsAsync(connection, string.Join(" AND ", terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"")), exactPhrase: false, pool, keywordHits);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch keyword matches from SQLite for query {Query}", query);
        }
        return keywordHits;
    }

    /// <summary>
    /// Loads titles from database for all candidate URLs gathered in semantic and keyword passes.
    /// </summary>
    /// <param name="vectorHits">The collection of vector hits.</param>
    /// <param name="keywordHits">The collection of keyword hits.</param>
    /// <returns>A dictionary map of URLs to their titles.</returns>
    private async Task<Dictionary<string, string?>> LoadTitlesAsync(
        IReadOnlyCollection<VectorCandidate> vectorHits, IReadOnlyCollection<KeywordCandidate> keywordHits)
    {
        var titles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in vectorHits) urls.Add(v.Url);
            foreach (var k in keywordHits) urls.Add(k.Url);
            if (urls.Count == 0) return titles;

            using var connection = new SqliteConnection(_dbConfig.ConnectionString);
            await connection.OpenAsync();
            await LoadTitlesAsync(connection, urls, titles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch result titles from SQLite.");
        }
        return titles;
    }

    /// <summary>
    /// Queries the full-text search index with the specified match expression and collects candidates.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="matchExpr">The FTS5 MATCH expression query.</param>
    /// <param name="exactPhrase"><c>true</c> if this matches the query literally; otherwise, <c>false</c>.</param>
    /// <param name="limit">The maximum number of rows to retrieve.</param>
    /// <param name="into">The list to append matches to.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private static async Task CollectKeywordHitsAsync(
        SqliteConnection connection, string matchExpr, bool exactPhrase, int limit, List<KeywordCandidate> into)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT c.Url, c.Text, c.IsHeading
            FROM text_chunks_fts f
            JOIN text_chunks c ON f.Id = c.Id
            WHERE text_chunks_fts MATCH @query
            ORDER BY f.rank
            LIMIT @limit";
        command.Parameters.AddWithValue("@query", matchExpr);
        command.Parameters.AddWithValue("@limit", limit);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            bool isHeading = !reader.IsDBNull(2) && reader.GetBoolean(2);
            into.Add(new KeywordCandidate(reader.GetString(0), reader.GetString(1), isHeading, exactPhrase));
        }
    }

    /// <summary>
    /// Queries the database in batches to resolve titles for the specified candidate URLs.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="urls">The set of URLs to fetch titles for.</param>
    /// <param name="into">The target dictionary to store the retrieved titles.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private static async Task LoadTitlesAsync(
        SqliteConnection connection, IReadOnlyCollection<string> urls, Dictionary<string, string?> into)
    {
        if (urls.Count == 0) return;

        // Batch the IN-list so we never approach SQLite's bound-parameter ceiling.
        const int batchSize = 400;
        var list = urls as IList<string> ?? urls.ToList();
        for (int start = 0; start < list.Count; start += batchSize)
        {
            int count = Math.Min(batchSize, list.Count - start);
            using var command = connection.CreateCommand();
            var sb = new StringBuilder("SELECT Url, Title FROM CrawlState WHERE Url IN (");
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                var name = "@u" + i;
                sb.Append(name);
                command.Parameters.AddWithValue(name, list[start + i]);
            }
            sb.Append(')');
            command.CommandText = sb.ToString();

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                into[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
    }




}
