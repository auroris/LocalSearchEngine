using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

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
    /// Hybrid search returning every "close enough" result (vector hits at or above
    /// the similarity threshold, plus all exact keyword matches) — no count cap.
    /// Ranking and thresholding live in <see cref="SearchRanker"/>.
    /// </summary>
    public async Task<SearchResponse> SearchAsync(string query)
    {
        int pool = _settings.CandidatePoolSize;

        // Semantic candidates: the connector returns nearest-first; Score is cosine distance.
        // Queries go through EmbedQuery so BGE's retrieval instruction is applied (passages
        // were indexed raw via Embed).
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

        // Keyword candidates from the FTS5 index, in two tiers, plus per-URL titles.
        var keywordHits = new List<KeywordCandidate>();
        var titles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var connection = new SqliteConnection(_dbConfig.ConnectionString);
            await connection.OpenAsync();

            // Tier 1: verbatim phrase match.
            await CollectKeywordHitsAsync(connection, BuildPhraseMatch(query), exactPhrase: true, pool, keywordHits);

            // Tier 2: looser all-terms (AND) match. Skipped for single-term queries, where
            // it would just duplicate the phrase tier.
            var terms = ExtractTerms(query);
            if (terms.Count > 1)
            {
                await CollectKeywordHitsAsync(connection, BuildAndMatch(terms), exactPhrase: false, pool, keywordHits);
            }

            // Titles for every candidate URL feed the title boost and the result list.
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in vectorHits) urls.Add(v.Url);
            foreach (var k in keywordHits) urls.Add(k.Url);
            await LoadTitlesAsync(connection, urls, titles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch keyword matches/titles from SQLite for query {Query}", query);
        }

        var items = SearchRanker.Rank(vectorHits, keywordHits, query, _settings, titles);
        return new SearchResponse { Items = items, TotalMatches = items.Count };
    }

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

    /// <summary>A single quoted FTS5 phrase: the whole query matched verbatim and in order.</summary>
    private static string BuildPhraseMatch(string query) => "\"" + query.Replace("\"", "\"\"") + "\"";

    /// <summary>An FTS5 expression requiring every term (each quoted), in any order/position.</summary>
    private static string BuildAndMatch(IEnumerable<string> terms) =>
        string.Join(" ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));

    /// <summary>Splits a query into word tokens (letters/digits), discarding punctuation.</summary>
    private static List<string> ExtractTerms(string query)
    {
        var terms = new List<string>();
        foreach (Match m in Regex.Matches(query, @"\w+")) terms.Add(m.Value);
        return terms;
    }
}
