using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LocalSearchEngine.Core.Crawling;

/// <summary>
/// Provides database access operations for managing crawl state, sitemaps, and the frontier queue in SQLite.
/// </summary>
public static class CrawlStore
{
    /// <summary>
    /// Creates the database tables, triggers, and indices for crawl state and full-text search mirrors if they do not exist.
    /// </summary>
    /// <param name="connectionString">The connection string to the SQLite database.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the required <c>text_chunks</c> table is not found in the schema.</exception>
    public static async Task EnsureSchemaAsync(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='text_chunks'";
            if (await check.ExecuteScalarAsync() is null)
            {
                throw new InvalidOperationException(
                    "The 'text_chunks' table is missing. Call VectorSearchService.EnsureCreatedAsync() before CrawlerService.EnsureCreatedAsync().");
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                PRAGMA journal_mode=WAL;

                CREATE TABLE IF NOT EXISTS CrawlState (
                    Url TEXT PRIMARY KEY,
                    LastCrawled DATETIME,
                    StatusCode INTEGER,
                    ETag TEXT,
                    LastModified TEXT,
                    Title TEXT,
                    ContentHash TEXT
                );

                -- Outlinks per page, so an incremental re-crawl can keep growing the frontier
                -- even when a page returns 304/unchanged (we never re-parse its HTML then).
                CREATE TABLE IF NOT EXISTS CrawlLinks (
                    FromUrl TEXT NOT NULL,
                    ToUrl TEXT NOT NULL,
                    PRIMARY KEY (FromUrl, ToUrl)
                );

                -- A snapshot of the pending queue, written when a run is interrupted so the
                -- next run can resume instead of starting the whole site over.
                CREATE TABLE IF NOT EXISTS CrawlFrontier (
                    Url TEXT PRIMARY KEY,
                    Seq INTEGER
                );

                -- porter stemming over unicode61 so 'running' matches 'run', 'guides' matches
                -- 'guide', etc. The URL isn't stored here: keyword hits join back to
                -- text_chunks by Id, so a second copy of every URL would just waste space.
                CREATE VIRTUAL TABLE IF NOT EXISTS text_chunks_fts USING fts5(Id UNINDEXED, Text, tokenize='porter unicode61');

                CREATE TRIGGER IF NOT EXISTS text_chunks_ai AFTER INSERT ON text_chunks BEGIN
                  INSERT INTO text_chunks_fts(Id, Text) VALUES (new.Id, new.Text);
                END;

                CREATE TRIGGER IF NOT EXISTS text_chunks_ad AFTER DELETE ON text_chunks BEGIN
                  DELETE FROM text_chunks_fts WHERE Id = old.Id;
                END;

                CREATE TRIGGER IF NOT EXISTS text_chunks_au AFTER UPDATE ON text_chunks BEGIN
                  DELETE FROM text_chunks_fts WHERE Id = old.Id;
                  INSERT INTO text_chunks_fts(Id, Text) VALUES (new.Id, new.Text);
                END;
            ";
            await command.ExecuteNonQueryAsync();
        }

        // Index ContentHash so a re-crawl can spot a URL whose byte-identical content is
        // already indexed under a different URL and alias it.
        using (var index = connection.CreateCommand())
        {
            index.CommandText = "CREATE INDEX IF NOT EXISTS idx_crawlstate_contenthash ON CrawlState(ContentHash);";
            await index.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Retrieves the ETag, Last-Modified, and ContentHash validators for a crawled URL.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The URL whose crawl state to retrieve.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple containing the ETag, LastModified, and ContentHash, each null if not present.</returns>
    public static async Task<(string? ETag, string? LastModified, string? ContentHash)> GetCrawlStateAsync(SqliteConnection connection, string url, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ETag, LastModified, ContentHash FROM CrawlState WHERE Url = @Url";
        cmd.Parameters.AddWithValue("@Url", url);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        return (null, null, null);
    }

    /// <summary>
    /// Checks whether the specified URL has any indexed text chunks in the database.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The URL to verify.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> if the URL has at least one chunk; otherwise, <c>false</c>.</returns>
    public static async Task<bool> UrlHasChunksAsync(SqliteConnection connection, string url, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM text_chunks WHERE Url = @Url LIMIT 1";
        cmd.Parameters.AddWithValue("@Url", url);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// Searches for a duplicate URL containing identical content hash that has already been indexed.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="contentHash">The hash of the page body.</param>
    /// <param name="excludeUrl">The URL to exclude from the duplicate search.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The URL of the duplicate page, or <c>null</c> if not found.</returns>
    public static async Task<string?> FindIndexedDuplicateAsync(SqliteConnection connection, string contentHash, string excludeUrl, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT cs.Url FROM CrawlState cs
            WHERE cs.ContentHash = @Hash AND cs.Url <> @Url
              AND EXISTS (SELECT 1 FROM text_chunks tc WHERE tc.Url = cs.Url)
            LIMIT 1";
        cmd.Parameters.AddWithValue("@Hash", contentHash);
        cmd.Parameters.AddWithValue("@Url", excludeUrl);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>
    /// Records crawl metadata (status code, headers, title, content hash) for a successfully crawled URL.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The URL of the crawled page.</param>
    /// <param name="statusCode">The HTTP status code of the response.</param>
    /// <param name="eTag">The ETag header value, if any.</param>
    /// <param name="lastModified">The Last-Modified header value, if any.</param>
    /// <param name="title">The page title, if any.</param>
    /// <param name="contentHash">The SHA256 content hash of the page body.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task RecordCrawlStateAsync(SqliteConnection connection, string url, int statusCode, string? eTag, string? lastModified, string? title, string? contentHash, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO CrawlState (Url, LastCrawled, StatusCode, ETag, LastModified, Title, ContentHash)
            VALUES (@Url, @LastCrawled, @StatusCode, @ETag, @LastModified, @Title, @ContentHash)
            ON CONFLICT(Url) DO UPDATE SET
                LastCrawled = excluded.LastCrawled,
                StatusCode = excluded.StatusCode,
                ETag = excluded.ETag,
                LastModified = excluded.LastModified,
                Title = excluded.Title,
                ContentHash = excluded.ContentHash;";

        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@LastCrawled", DateTime.UtcNow);
        command.Parameters.AddWithValue("@StatusCode", statusCode);
        command.Parameters.AddWithValue("@ETag", eTag ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@LastModified", lastModified ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Title", title ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ContentHash", contentHash ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Records that a URL was visited, updating timestamp and status code, and optionally clears stored metadata.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The URL of the page visited.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="clearMetadata"><c>true</c> to reset headers and content hash (e.g. for redirects/deletions); otherwise, <c>false</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task RecordVisitAsync(SqliteConnection connection, string url, int statusCode, bool clearMetadata, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        if (clearMetadata)
        {
            command.CommandText = @"
                INSERT INTO CrawlState (Url, LastCrawled, StatusCode, Title, ETag, LastModified, ContentHash)
                VALUES (@Url, @LastCrawled, @StatusCode, NULL, NULL, NULL, NULL)
                ON CONFLICT(Url) DO UPDATE SET
                    LastCrawled = excluded.LastCrawled,
                    StatusCode = excluded.StatusCode,
                    Title = NULL,
                    ETag = NULL,
                    LastModified = NULL,
                    ContentHash = NULL;";
        }
        else
        {
            command.CommandText = @"
                INSERT INTO CrawlState (Url, LastCrawled, StatusCode)
                VALUES (@Url, @LastCrawled, @StatusCode)
                ON CONFLICT(Url) DO UPDATE SET
                    LastCrawled = excluded.LastCrawled,
                    StatusCode = excluded.StatusCode;";
        }
        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@LastCrawled", DateTime.UtcNow);
        command.Parameters.AddWithValue("@StatusCode", statusCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Stores the list of outlinks discovered on a page, replacing any existing links for that page in a transaction.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="fromUrl">The source page URL.</param>
    /// <param name="outlinks">The collection of target outlink URLs.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task StoreOutlinksAsync(SqliteConnection connection, string fromUrl, IReadOnlyCollection<string> outlinks, CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM CrawlLinks WHERE FromUrl = @From";
            delete.Parameters.AddWithValue("@From", fromUrl);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        if (outlinks.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO CrawlLinks (FromUrl, ToUrl) VALUES (@From, @To)";
            var fromParam = insert.Parameters.Add("@From", SqliteType.Text);
            var toParam = insert.Parameters.Add("@To", SqliteType.Text);
            fromParam.Value = fromUrl;
            foreach (var to in outlinks)
            {
                toParam.Value = to;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes all stored outlinks associated with the specified source URL.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="fromUrl">The source page URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task DeleteOutlinksAsync(SqliteConnection connection, string fromUrl, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CrawlLinks WHERE FromUrl = @From";
        command.Parameters.AddWithValue("@From", fromUrl);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Retrieves the list of outlinks stored for the specified page.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The source page URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of outlink URL strings.</returns>
    public static async Task<List<string>> GetStoredOutlinksAsync(SqliteConnection connection, string url, CancellationToken cancellationToken)
    {
        var links = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ToUrl FROM CrawlLinks WHERE FromUrl = @From";
        command.Parameters.AddWithValue("@From", url);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(reader.GetString(0));
        }
        return links;
    }

    /// <summary>
    /// Reads the saved snapshot of the crawl frontier queue.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of URLs ordered by their queue sequence.</returns>
    public static async Task<List<string>> ReadFrontierAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT Url FROM CrawlFrontier ORDER BY Seq";
        using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            urls.Add(reader.GetString(0));
        }
        return urls;
    }

    /// <summary>
    /// Clears the saved crawl frontier queue snapshot.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task ClearFrontierAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var clear = connection.CreateCommand();
        clear.CommandText = "DELETE FROM CrawlFrontier";
        await clear.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Saves the current crawl frontier queue to the database in a transaction.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="urls">The collection of URLs to save.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task SaveFrontierAsync(SqliteConnection connection, IEnumerable<string> urls, CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM CrawlFrontier";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO CrawlFrontier (Url, Seq) VALUES (@Url, @Seq)";
            var urlParam = insert.Parameters.Add("@Url", SqliteType.Text);
            var seqParam = insert.Parameters.Add("@Seq", SqliteType.Integer);
            int seq = 0;
            foreach (var url in urls)
            {
                urlParam.Value = url;
                seqParam.Value = seq++;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Optimizes the database indexing structure, vacuuming it if significant space is free.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="logger">The logger instance.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task OptimizeDatabaseAsync(SqliteConnection connection, ILogger logger)
    {
        try
        {
            using (var optimize = connection.CreateCommand())
            {
                optimize.CommandText = "PRAGMA optimize;";
                await optimize.ExecuteNonQueryAsync();
            }

            long freelist = await ReadPragmaLongAsync(connection, "PRAGMA freelist_count;");
            long pageCount = await ReadPragmaLongAsync(connection, "PRAGMA page_count;");
            if (pageCount > 1000 && freelist > pageCount / 4)
            {
                logger.LogInformation("Vacuuming database ({Free}/{Total} pages free)...", freelist, pageCount);
                using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM;";
                await vacuum.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to optimize database.");
        }
    }

    /// <summary>
    /// Reads a database pragma query that returns an integer/long value.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="pragma">The pragma query statement.</param>
    /// <returns>The long value returned by the pragma, or 0 if it failed or returned null.</returns>
    private static async Task<long> ReadPragmaLongAsync(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = pragma;
        var value = await command.ExecuteScalarAsync();
        return value is long l ? l : 0L;
    }
}
