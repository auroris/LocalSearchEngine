using SmartComponents.LocalEmbeddings;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

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
    private readonly LocalEmbedder _embedder;
    private readonly VectorStoreCollection<string, TextChunkRecord> _collection;
    private readonly DatabaseConfig _dbConfig;
    private readonly SearchSettings _settings;
    private readonly ILogger<VectorSearchService> _logger;

    public VectorSearchService(LocalEmbedder embedder, VectorStore vectorStore, DatabaseConfig dbConfig, IOptions<SearchSettings> options, ILogger<VectorSearchService> logger)
    {
        _embedder = embedder;
        _collection = vectorStore.GetCollection<string, TextChunkRecord>("text_chunks");
        _dbConfig = dbConfig;
        _settings = options.Value;
        _logger = logger;
    }

    public Task InitializeAsync()
    {
        return _collection.EnsureCollectionExistsAsync();
    }

    public async Task DeleteUrlChunksAsync(string url)
    {
        try
        {
            // Using Microsoft.Data.Sqlite directly to execute deletion by Url metadata.
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_dbConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM text_chunks WHERE Url = @Url";
            command.Parameters.AddWithValue("@Url", url);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete URL chunks for {Url}", url);
            throw;
        }
    }

    public async Task IndexUrlChunksAsync(string url, string fullText, bool isHeading = false)
    {
        var words = fullText.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        int chunkSize = 150;
        int overlap = 30;
        
        for (int i = 0; i < words.Length; i += (chunkSize - overlap))
        {
            var chunkWords = words.Skip(i).Take(chunkSize);
            if (!chunkWords.Any()) break;
            
            var chunkText = string.Join(" ", chunkWords);
            
            // Generate embedding locally using the CPU
            var embedding = _embedder.Embed(chunkText);
            
            var record = new TextChunkRecord
            {
                Id = Guid.NewGuid().ToString(),
                Url = url,
                Text = chunkText,
                IsHeading = isHeading,
                Embedding = embedding.Values
            };

            await _collection.UpsertAsync(record);
        }
    }

    public async Task<PagedSearchResult> SearchAsync(string query, double? minScore = null)
    {
        double effectiveMinScore = minScore ?? _settings.MinScore;
        var queryEmbedding = _embedder.Embed(query).Values;
        
        var searchOptions = new VectorSearchOptions<TextChunkRecord>
        {
        };

        // Fetch enough results to get good semantic coverage
        int fetchLimit = _settings.SearchFetchLimit;
        var searchResult = _collection.SearchAsync(queryEmbedding, fetchLimit, searchOptions);
        
        var results = new List<SearchResultItem>();
        
        await foreach (var record in searchResult)
        {
            double score = record.Score ?? 0.0;
            
            if (record.Record.IsHeading)
            {
                score += 0.3; // Boost for headings
            }

            // Give a score boost if the query term appears directly in the text
            if (record.Record.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.2;
            }

            // Give a score boost if the file name contains the query
            if (Uri.TryCreate(record.Record.Url, UriKind.Absolute, out var uri))
            {
                var segment = uri.Segments.LastOrDefault()?.TrimEnd('/');
                if (segment != null)
                {
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(segment);
                    if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 0.4; // Strong boost for URL filename match
                    }
                }
            }

            results.Add(new SearchResultItem
            {
                Url = record.Record.Url,
                Text = record.Record.Text,
                Score = score
            });
        }

        try
        {
            // Fetch exact matches directly from SQLite to ensure they are considered
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_dbConfig.ConnectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Url, Text, IsHeading FROM text_chunks WHERE Text LIKE @query LIMIT @limit";
            command.Parameters.AddWithValue("@query", $"%{query}%");
            command.Parameters.AddWithValue("@limit", fetchLimit);
            
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                bool isHeading = false;
                if (reader.FieldCount >= 3 && !reader.IsDBNull(2))
                {
                    try { isHeading = reader.GetInt32(2) != 0; } catch { }
                }

                double score = 1.0;
                if (isHeading) score += 0.3;

                var url = reader.GetString(0);
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    var segment = uri.Segments.LastOrDefault()?.TrimEnd('/');
                    if (segment != null)
                    {
                        var fileName = System.IO.Path.GetFileNameWithoutExtension(segment);
                        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 0.4;
                        }
                    }
                }

                results.Add(new SearchResultItem
                {
                    Url = url,
                    Text = reader.GetString(1),
                    Score = score // Assign a high baseline score so exact matches surface to the top
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch exact matches from SQLite for query {Query}", query);
        }

        var distinctResults = results
            .GroupBy(r => r.Url)
            .Select(g => g.OrderByDescending(r => r.Score).First())
            .Where(r => r.Score >= effectiveMinScore)
            .OrderByDescending(r => r.Score)
            .ToList();

        return new PagedSearchResult
        {
            Items = distinctResults,
            TotalMatches = distinctResults.Count,
            Page = 1,
            PageSize = distinctResults.Count
        };
    }
}

public class SearchResultItem
{
    public string Url { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double Score { get; set; }
}

public class PagedSearchResult
{
    public List<SearchResultItem> Items { get; set; } = new();
    public int TotalMatches { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
