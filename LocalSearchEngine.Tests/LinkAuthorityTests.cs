using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Crawling.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LocalSearchEngine.Tests;

public sealed class LinkAuthorityTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"lse_links_{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_dbPath}";

    private async Task<SqliteConnection> CreateDatabaseAsync(bool oldCrawlState = false)
    {
        using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE text_chunks (
                    Id TEXT PRIMARY KEY,
                    Url TEXT NOT NULL,
                    Text TEXT NOT NULL,
                    IsHeading INTEGER NOT NULL DEFAULT 0
                );" + (oldCrawlState
                    ? @"
                CREATE TABLE CrawlState (
                    Url TEXT PRIMARY KEY,
                    LastCrawled DATETIME,
                    StatusCode INTEGER,
                    ETag TEXT,
                    LastModified TEXT,
                    Title TEXT,
                    ContentHash TEXT,
                    DocKind INTEGER
                );"
                    : string.Empty);
            await command.ExecuteNonQueryAsync();
        }

        await CrawlStore.EnsureSchemaAsync(ConnectionString);
        var result = new SqliteConnection(ConnectionString);
        await result.OpenAsync();
        return result;
    }

    [Fact]
    public async Task Existing_crawl_state_is_migrated_for_authority_scores()
    {
        await using var connection = await CreateDatabaseAsync(oldCrawlState: true);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(CrawlState)";
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));

        Assert.Contains("PageRank", columns);
        Assert.Contains("Authority", columns);
        Assert.Contains("LinkContextVersion", columns);
    }

    [Fact]
    public async Task Recording_parsed_content_marks_link_context_extraction_current()
    {
        await using var connection = await CreateDatabaseAsync();
        const string url = "https://site/page";

        await CrawlStore.RecordCrawlStateAsync(
            connection, url, 200, "\"v1\"", null, "Page", "hash", DocKind.Html);

        var state = await CrawlStore.GetCrawlStateAsync(connection, url);
        Assert.Equal(CrawlStore.CurrentLinkContextVersion, state.LinkContextVersion);
    }

    [Fact]
    public async Task Link_context_is_mirrored_to_fts_and_replaced_with_source_links()
    {
        await using var connection = await CreateDatabaseAsync();
        const string source = "https://site/source";
        const string target = "https://site/target";
        var evidence = new[]
        {
            new LinkEvidence(
                target,
                "PKI maintenance procedure",
                "Use this procedure for certificate renewal.",
                "Certificate Management")
        };

        await CrawlStore.StoreLinksAsync(
            connection, source, [target], [], evidence, "Server Operations");

        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM LinkContexts"));
        Assert.Equal(1, Scalar(connection,
            "SELECT COUNT(*) FROM link_contexts_fts WHERE link_contexts_fts MATCH 'certificate'"));

        await CrawlStore.StoreLinksAsync(connection, source, [], []);

        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM LinkContexts"));
        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM link_contexts_fts"));
    }

    [Fact]
    public async Task PageRank_rewards_pages_endorsed_by_multiple_indexed_pages()
    {
        await using var connection = await CreateDatabaseAsync();
        const string a = "https://site/a";
        const string b = "https://site/b";
        const string endorsed = "https://site/endorsed";
        const string unindexed = "https://site/unindexed";

        await InsertIndexedPageAsync(connection, "a", a);
        await InsertIndexedPageAsync(connection, "b", b);
        await InsertIndexedPageAsync(connection, "endorsed", endorsed);
        await CrawlStore.RecordVisitAsync(connection, unindexed, 200, clearMetadata: false);
        await CrawlStore.StoreLinksAsync(connection, a, [endorsed, unindexed], []);
        await CrawlStore.StoreLinksAsync(connection, b, [endorsed], []);

        var summary = await CrawlStore.RecomputePageRankAsync(connection);

        Assert.Equal(3, summary.NodeCount);
        Assert.Equal(2, summary.EdgeCount); // the edge to a URL with no chunks is not part of the graph
        Assert.InRange(summary.Iterations, 1, 100);
        Assert.Equal(1.0, ReadDouble(connection,
            "SELECT Authority FROM CrawlState WHERE Url = @Url", endorsed), 10);
        Assert.Equal(0.0, ReadDouble(connection,
            "SELECT Authority FROM CrawlState WHERE Url = @Url", a), 10);
        Assert.Equal(1.0, ReadDouble(connection,
            "SELECT SUM(PageRank) FROM CrawlState WHERE Url <> @Url", unindexed), 10);
        Assert.Equal(0.0, ReadDouble(connection,
            "SELECT Authority FROM CrawlState WHERE Url = @Url", unindexed), 10);
    }

    private static async Task InsertIndexedPageAsync(
        SqliteConnection connection, string id, string url)
    {
        await CrawlStore.RecordVisitAsync(connection, url, 200, clearMetadata: false);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO text_chunks (Id, Url, Text, IsHeading)
            VALUES (@Id, @Url, @Text, 0)";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@Text", id);
        await command.ExecuteNonQueryAsync();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    private static double ReadDouble(
        SqliteConnection connection, string sql, string url)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@Url", url);
        return Convert.ToDouble(command.ExecuteScalar() ?? 0.0);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* temporary test file; best effort */ }
    }
}
