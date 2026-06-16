using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LocalSearchEngine.Core.Crawling.Storage;

/// <summary>
/// Provides database access operations for managing crawl state and per-page outlinks in SQLite.
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

                CREATE TABLE CrawlState (
                    Url TEXT PRIMARY KEY,
                    LastCrawled DATETIME,
                    StatusCode INTEGER,
                    ETag TEXT,
                    LastModified TEXT,
                    Title TEXT,
                    ContentHash TEXT
                );

                -- Every link the crawler encounters: in-scope outlinks AND off-site links, each
                -- with a verified Status (0 Unknown, 1 Ok, 2 Redirect, 3 Error) and when it was
                -- last set. In-scope rows (External=0) drive frontier re-derivation on 304/unchanged
                -- pages — we never re-parse their HTML then — while off-site rows let the end-of-crawl
                -- pass verify links even on pages whose content didn't change this run.
                CREATE TABLE LinkIndex (
                    FromUrl TEXT NOT NULL,
                    ToUrl TEXT NOT NULL,
                    External INTEGER NOT NULL DEFAULT 0,
                    Status INTEGER NOT NULL DEFAULT 0,
                    StatusCode INTEGER NOT NULL DEFAULT 0,
                    LastUpdated DATETIME,
                    PRIMARY KEY (FromUrl, ToUrl)
                ) WITHOUT ROWID;

                -- After visiting a page, the status of every link pointing at it is set in one
                -- statement (WHERE ToUrl = ...), so ToUrl needs its own index.
                CREATE INDEX idx_linkindex_tourl ON LinkIndex(ToUrl);

                -- The end-of-crawl verify and report scans filter on Status and LastUpdated.
                CREATE INDEX idx_linkindex_status ON LinkIndex(Status, LastUpdated);

                -- Verify links that haven't been updated this crawl run.
                CREATE INDEX idx_linkindex_lastupdated ON LinkIndex(LastUpdated);

                -- porter stemming over unicode61 so 'running' matches 'run', 'guides' matches
                -- 'guide', etc. The URL isn't stored here: keyword hits join back to
                -- text_chunks by Id, so a second copy of every URL would just waste space.
                CREATE VIRTUAL TABLE text_chunks_fts USING fts5(Id UNINDEXED, Text, tokenize='porter unicode61');

                CREATE TRIGGER text_chunks_ai AFTER INSERT ON text_chunks BEGIN
                  INSERT INTO text_chunks_fts(Id, Text) VALUES (new.Id, new.Text);
                END;

                CREATE TRIGGER text_chunks_ad AFTER DELETE ON text_chunks BEGIN
                  DELETE FROM text_chunks_fts WHERE Id = old.Id;
                END;

                CREATE TRIGGER text_chunks_au AFTER UPDATE ON text_chunks BEGIN
                  DELETE FROM text_chunks_fts WHERE Id = old.Id;
                  INSERT INTO text_chunks_fts(Id, Text) VALUES (new.Id, new.Text);
                END;

                -- Create a covering index on ContentHash + Url
                CREATE INDEX idx_crawlstate_contenthash_url ON CrawlState(ContentHash, Url);

                -- Index LastCrawled to optimize locating abandoned pages and fetching max crawl timestamps
                CREATE INDEX idx_crawlstate_lastcrawled ON CrawlState(LastCrawled);

                -- The vector connector creates text_chunks with no index on Url, yet every page
                -- visit filters on it (chunk deletes, the has-chunks probe, the duplicate-content
                -- EXISTS) — without this, each of those is a full scan of the largest table.
                CREATE INDEX idx_text_chunks_url ON text_chunks(Url);
            ";
            await command.ExecuteNonQueryAsync();
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
    /// Stores every link discovered on a page — in-scope outlinks and off-site links alike —
    /// replacing any existing links for that page in a transaction. New rows start
    /// <see cref="Reporting.LinkStatus.Unknown"/> with no <c>LastUpdated</c>; their status is
    /// filled in when the destination is visited this run, or by the end-of-crawl verification pass.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="fromUrl">The source page URL.</param>
    /// <param name="inScopeLinks">Target URLs within the crawl scope (stored with <c>External=0</c>).</param>
    /// <param name="offsiteLinks">Off-site target URLs (stored with <c>External=1</c>).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task StoreLinksAsync(SqliteConnection connection, string fromUrl, IReadOnlyCollection<string> inScopeLinks, IReadOnlyCollection<string> offsiteLinks, CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM LinkIndex WHERE FromUrl = @From";
            delete.Parameters.AddWithValue("@From", fromUrl);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        if (inScopeLinks.Count > 0 || offsiteLinks.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO LinkIndex (FromUrl, ToUrl, External, Status, StatusCode, LastUpdated) VALUES (@From, @To, @External, 0, 0, NULL)";
            var fromParam = insert.Parameters.Add("@From", SqliteType.Text);
            var toParam = insert.Parameters.Add("@To", SqliteType.Text);
            var externalParam = insert.Parameters.Add("@External", SqliteType.Integer);
            fromParam.Value = fromUrl;

            externalParam.Value = 0;
            foreach (var to in inScopeLinks)
            {
                toParam.Value = to;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            externalParam.Value = 1;
            foreach (var to in offsiteLinks)
            {
                toParam.Value = to;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes all stored links (in-scope and off-site) originating from the specified source URL.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="fromUrl">The source page URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task DeleteLinksAsync(SqliteConnection connection, string fromUrl, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM LinkIndex WHERE FromUrl = @From";
        command.Parameters.AddWithValue("@From", fromUrl);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Retrieves the in-scope outlinks stored for the specified page, used to re-derive the frontier
    /// when a page is unchanged (off-site links are excluded — they are never crawled).
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The source page URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A list of in-scope outlink URL strings.</returns>
    public static async Task<List<string>> GetStoredOutlinksAsync(SqliteConnection connection, string url, CancellationToken cancellationToken)
    {
        var links = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ToUrl FROM LinkIndex WHERE FromUrl = @From AND External = 0";
        command.Parameters.AddWithValue("@From", url);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(reader.GetString(0));
        }
        return links;
    }

    /// <summary>
    /// Sets the verified status of every link pointing at the given destination URL, stamping the
    /// current time. Called once per page the crawler resolves, so all the links that led to that
    /// page reflect how it last responded.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="toUrl">The destination URL whose inbound links to update.</param>
    /// <param name="status">The <see cref="Reporting.LinkStatus"/> integer value.</param>
    /// <param name="statusCode">The HTTP status observed (0 for a connection-level failure).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task UpdateLinkStatusByDestinationAsync(SqliteConnection connection, string toUrl, int status, int statusCode, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LinkIndex SET Status = @Status, StatusCode = @StatusCode, LastUpdated = @Now WHERE ToUrl = @To";
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@StatusCode", statusCode);
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow);
        command.Parameters.AddWithValue("@To", toUrl);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Lists links not determined this run — those whose <c>LastUpdated</c> predates the crawl start
    /// or is unset — so the end-of-crawl pass can verify them. Spans the whole table; the caller
    /// narrows to the links found on pages in the current crawl's scope.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="crawlStartUtc">The crawl start; rows last updated before it are returned.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The (origin, destination, external) tuples awaiting verification.</returns>
    public static async Task<List<(string FromUrl, string ToUrl, bool External)>> GetLinksToVerifyAsync(SqliteConnection connection, DateTime crawlStartUtc, CancellationToken cancellationToken)
    {
        var rows = new List<(string, string, bool)>();
        using var command = connection.CreateCommand();
        // LastUpdated is stored as sortable ISO-8601 text, so the comparison below is sound.
        command.CommandText = "SELECT FromUrl, ToUrl, External FROM LinkIndex WHERE LastUpdated IS NULL OR LastUpdated < @Cutoff";
        command.Parameters.AddWithValue("@Cutoff", crawlStartUtc);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        }
        return rows;
    }

    /// <summary>
    /// Lists links that resolved to a redirect or an error and were determined this run (their
    /// <c>LastUpdated</c> is at or after the crawl start), for the broken/redirected-links report.
    /// The caller narrows to the links found on pages in the current crawl's scope.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="crawlStartUtc">The crawl start; only rows updated at or after it are returned.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The (origin, destination, external, status, statusCode) tuples to report.</returns>
    public static async Task<List<(string FromUrl, string ToUrl, bool External, int Status, int StatusCode)>> GetReportableLinksAsync(SqliteConnection connection, DateTime crawlStartUtc, CancellationToken cancellationToken)
    {
        var rows = new List<(string, string, bool, int, int)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FromUrl, ToUrl, External, Status, StatusCode FROM LinkIndex WHERE Status IN (2, 3) AND LastUpdated >= @Cutoff";
        command.Parameters.AddWithValue("@Cutoff", crawlStartUtc);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3), reader.GetInt32(4)));
        }
        return rows;
    }

    /// <summary>
    /// Lists URLs whose last visit predates the given cutoff (or that were never stamped),
    /// i.e. URLs a crawl that started at the cutoff did not reach.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="cutoffUtc">The crawl start time; rows last crawled before it are returned.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list of URLs not visited since the cutoff.</returns>
    public static async Task<List<string>> GetUrlsNotCrawledSinceAsync(SqliteConnection connection, DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        using var command = connection.CreateCommand();
        // LastCrawled is stored as sortable ISO-8601 text, so the comparison below is sound.
        command.CommandText = "SELECT Url FROM CrawlState WHERE LastCrawled IS NULL OR LastCrawled < @Cutoff";
        command.Parameters.AddWithValue("@Cutoff", cutoffUtc);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            urls.Add(reader.GetString(0));
        }
        return urls;
    }

    /// <summary>
    /// Lists crawl-state URLs whose string begins with the given prefix (e.g. an origin's
    /// "scheme://host[:port]"). The prefix is treated literally — LIKE metacharacters in it are
    /// escaped — and the match is coarse: a prefix like "https://example.com" also matches a
    /// different port or a look-alike host such as "example.com.evil.com", so callers must still
    /// confirm the exact origin of each result.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="urlPrefix">The literal URL prefix to match.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list of crawl-state URLs starting with the prefix.</returns>
    public static async Task<List<string>> GetCrawledUrlsWithPrefixAsync(SqliteConnection connection, string urlPrefix, CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Url FROM CrawlState WHERE Url LIKE @Pattern ESCAPE '\\'";
        command.Parameters.AddWithValue("@Pattern", EscapeLike(urlPrefix) + "%");
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            urls.Add(reader.GetString(0));
        }
        return urls;
    }

    /// <summary>
    /// Escapes LIKE metacharacters (backslash, percent, underscore) so a string matches literally
    /// under an <c>ESCAPE '\'</c> clause. Backslash is escaped first to avoid double-escaping.
    /// </summary>
    /// <param name="value">The literal value to escape.</param>
    /// <returns>The escaped value, safe to embed in a LIKE pattern.</returns>
    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Deletes the crawl-state row for the specified URL.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The URL whose row to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task DeleteCrawlStateAsync(SqliteConnection connection, string url, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CrawlState WHERE Url = @Url";
        command.Parameters.AddWithValue("@Url", url);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Counts what is currently stored: distinct indexed URLs (those with text chunks) and total
    /// crawl-state rows. Used for the end-of-run statistics.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A tuple of the distinct indexed URL count and the crawl-state row count.</returns>
    public static async Task<(long IndexedUrls, long CrawlStateRows)> GetCountsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        long indexedUrls;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(DISTINCT Url) FROM text_chunks";
            indexedUrls = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        long crawlStateRows;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM CrawlState";
            crawlStateRows = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }

        return (indexedUrls, crawlStateRows);
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
