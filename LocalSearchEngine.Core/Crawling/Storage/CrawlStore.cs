using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LocalSearchEngine.Core.Crawling;
using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Storage;

/// <summary>
/// The crawler's SQL layer: one static home for every query and command it issues against SQLite,
/// keeping raw SQL out of the engine. It owns the schema it depends on — the <c>CrawlState</c> table
/// (per-URL status, cache validators, title, content hash, and link authority), the <c>LinkIndex</c>
/// graph, compact inbound-link context, and the FTS5 mirrors of body and link text with the triggers
/// that keep them in sync — created by <see cref="EnsureSchemaAsync"/> after the vector store has made
/// its <c>text_chunks</c> table. The rest are focused reads and writes over those tables: record a
/// visit, store or re-derive a page's links, find a duplicate by content hash, list URLs a crawl no
/// longer reaches, and so on. Every method works on a connection the caller owns and opens; the write
/// methods accept an optional <see cref="SqliteTransaction"/> so a caller can batch several of them
/// into one atomic unit (the consumer applies each page's writes that way).
/// </summary>
public static class CrawlStore
{
    /// <summary>
    /// Version of the persisted anchor/context extraction format. HTML rows below this version are
    /// fetched without conditional validators once so an existing database can backfill link text.
    /// </summary>
    public const int CurrentLinkContextVersion = 1;

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
                    ContentHash TEXT,
                    -- The DocKind enum value of the page's indexed content (Html/Pdf/Docx), so search
                    -- ranking can apply its configurable PDF/DOCX penalty without re-sniffing. NULL for rows
                    -- that were only visited, not indexed (redirects, 404s, unsupported types).
                    DocKind INTEGER,
                    -- Raw internal PageRank and its log-normalized [0,1] ranking feature.
                    PageRank REAL NOT NULL DEFAULT 0,
                    Authority REAL NOT NULL DEFAULT 0,
                    LinkContextVersion INTEGER NOT NULL DEFAULT 0
                );

                -- Every link the crawler encounters: in-scope outlinks AND off-site links, each
                -- with a verified Status (0 Unknown, 1 Ok, 2 Redirect, 3 Error) and when it was
                -- last set. In-scope rows (External=0) drive frontier re-derivation on 304/unchanged
                -- pages — we never re-parse their HTML then — while off-site rows let the end-of-crawl
                -- pass verify links even on pages whose content didn't change this run.
                CREATE TABLE IF NOT EXISTS LinkIndex (
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
                CREATE INDEX IF NOT EXISTS idx_linkindex_tourl ON LinkIndex(ToUrl);

                -- The end-of-crawl verify and report scans filter on Status and LastUpdated.
                CREATE INDEX IF NOT EXISTS idx_linkindex_status ON LinkIndex(Status, LastUpdated);

                -- Verify links that haven't been updated this crawl run.
                CREATE INDEX IF NOT EXISTS idx_linkindex_lastupdated ON LinkIndex(LastUpdated);

                -- Compact editorial descriptions of in-scope links. Several distinct contexts may
                -- exist for one source/target edge, while LinkIndex deliberately keeps one graph edge.
                CREATE TABLE IF NOT EXISTS LinkContexts (
                    Id INTEGER PRIMARY KEY,
                    FromUrl TEXT NOT NULL,
                    ToUrl TEXT NOT NULL,
                    AnchorText TEXT NOT NULL DEFAULT '',
                    ContextText TEXT NOT NULL DEFAULT '',
                    SectionHeading TEXT NOT NULL DEFAULT '',
                    SourceTitle TEXT NOT NULL DEFAULT '',
                    UNIQUE (FromUrl, ToUrl, AnchorText, ContextText, SectionHeading)
                );

                CREATE INDEX IF NOT EXISTS idx_linkcontexts_fromurl ON LinkContexts(FromUrl);
                CREATE INDEX IF NOT EXISTS idx_linkcontexts_tourl ON LinkContexts(ToUrl);

                -- Anchor text is the strongest field; context and the nearest section heading add
                -- vocabulary for terse labels such as details or click-here. Source title is
                -- retained as a weaker description of the referring page.
                CREATE VIRTUAL TABLE IF NOT EXISTS link_contexts_fts USING fts5(
                    Id UNINDEXED, AnchorText, ContextText, SectionHeading, SourceTitle,
                    tokenize='porter unicode61');

                CREATE TRIGGER IF NOT EXISTS link_contexts_ai AFTER INSERT ON LinkContexts BEGIN
                  INSERT INTO link_contexts_fts(Id, AnchorText, ContextText, SectionHeading, SourceTitle)
                  VALUES (new.Id, new.AnchorText, new.ContextText, new.SectionHeading, new.SourceTitle);
                END;

                CREATE TRIGGER IF NOT EXISTS link_contexts_ad AFTER DELETE ON LinkContexts BEGIN
                  DELETE FROM link_contexts_fts WHERE Id = old.Id;
                END;

                CREATE TRIGGER IF NOT EXISTS link_contexts_au AFTER UPDATE ON LinkContexts BEGIN
                  DELETE FROM link_contexts_fts WHERE Id = old.Id;
                  INSERT INTO link_contexts_fts(Id, AnchorText, ContextText, SectionHeading, SourceTitle)
                  VALUES (new.Id, new.AnchorText, new.ContextText, new.SectionHeading, new.SourceTitle);
                END;

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

                -- Create a covering index on ContentHash + Url
                CREATE INDEX IF NOT EXISTS idx_crawlstate_contenthash_url ON CrawlState(ContentHash, Url);

                -- Index LastCrawled to optimize locating abandoned pages and fetching max crawl timestamps
                CREATE INDEX IF NOT EXISTS idx_crawlstate_lastcrawled ON CrawlState(LastCrawled);

                -- The vector connector creates text_chunks with no index on Url, yet every page
                -- visit filters on it (chunk deletes, the has-chunks probe, the duplicate-content
                -- EXISTS) — without this, each of those is a full scan of the largest table.
                CREATE INDEX IF NOT EXISTS idx_text_chunks_url ON text_chunks(Url);
            ";
            await command.ExecuteNonQueryAsync();
        }

        // CREATE TABLE IF NOT EXISTS does not add columns to databases produced by older crawler
        // versions. These idempotent migrations make an existing graph immediately PageRank-ready.
        await EnsureColumnAsync(connection, "CrawlState", "PageRank", "REAL NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "CrawlState", "Authority", "REAL NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "CrawlState", "LinkContextVersion", "INTEGER NOT NULL DEFAULT 0");
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection, string tableName, string columnName, string declaration)
    {
        using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = $"PRAGMA table_info(\"{tableName}\")";
            using var reader = await inspect.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {declaration}";
        await alter.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Retrieves validators, content identity, document kind, and link-context schema version for a crawled URL.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The URL whose crawl state to retrieve.</param>
    /// <returns>The stored conditional-request and indexing state, or null/default values when absent.</returns>
    public static async Task<(
        string? ETag,
        string? LastModified,
        string? ContentHash,
        DocKind? DocKind,
        int LinkContextVersion)> GetCrawlStateAsync(SqliteConnection connection, string url)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ETag, LastModified, ContentHash, DocKind, LinkContextVersion
            FROM CrawlState WHERE Url = @Url";
        cmd.Parameters.AddWithValue("@Url", url);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : (DocKind?)reader.GetInt32(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4));
        }
        return (null, null, null, null, 0);
    }

    /// <summary>
    /// Retrieves when a URL was last visited by any crawl, in UTC. This is the incremental
    /// planner's coverage boundary: a feed entry whose publish date is at or before this moment was
    /// already seen in its current version.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The URL whose last visit to retrieve.</param>
    /// <returns>The last visit in UTC, or <c>null</c> if the URL has never been crawled.</returns>
    public static async Task<DateTime?> GetLastCrawledAsync(SqliteConnection connection, string url)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT LastCrawled FROM CrawlState WHERE Url = @Url AND LastCrawled IS NOT NULL";
        cmd.Parameters.AddWithValue("@Url", url);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync() && !reader.IsDBNull(0))
        {
            // Stored from DateTime.UtcNow; the ISO text round-trips without a kind, so restamp UTC.
            return DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
        }
        return null;
    }

    /// <summary>
    /// Checks whether the specified URL has any indexed text chunks in the database.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The URL to verify.</param>
    /// <returns><c>true</c> if the URL has at least one chunk; otherwise, <c>false</c>.</returns>
    public static async Task<bool> UrlHasChunksAsync(SqliteConnection connection, string url)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM text_chunks WHERE Url = @Url LIMIT 1";
        cmd.Parameters.AddWithValue("@Url", url);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    /// <summary>
    /// Searches for a duplicate URL containing identical content hash that has already been indexed.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="contentHash">The content hash to match against indexed pages.</param>
    /// <param name="excludeUrl">The URL to exclude from the duplicate search.</param>
    /// <returns>The URL of the duplicate page, or <c>null</c> if not found.</returns>
    public static async Task<string?> FindIndexedDuplicateAsync(SqliteConnection connection, string contentHash, string excludeUrl)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT cs.Url FROM CrawlState cs
            WHERE cs.ContentHash = @Hash AND cs.Url <> @Url
              AND EXISTS (SELECT 1 FROM text_chunks tc WHERE tc.Url = cs.Url)
            LIMIT 1";
        cmd.Parameters.AddWithValue("@Hash", contentHash);
        cmd.Parameters.AddWithValue("@Url", excludeUrl);
        return await cmd.ExecuteScalarAsync() as string;
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
    /// <param name="contentHash">The content hash of the page's extracted indexable content, if any.</param>
    /// <param name="docKind">The classified document kind of the indexed content, used by search ranking for its configurable non-HTML penalty.</param>
    /// <param name="transaction">An open transaction to enlist this write in, or <c>null</c> to run it standalone.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task RecordCrawlStateAsync(SqliteConnection connection, string url, int statusCode, string? eTag, string? lastModified, string? title, string? contentHash, DocKind docKind, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO CrawlState
                (Url, LastCrawled, StatusCode, ETag, LastModified, Title, ContentHash, DocKind, LinkContextVersion)
            VALUES
                (@Url, @LastCrawled, @StatusCode, @ETag, @LastModified, @Title, @ContentHash, @DocKind, @LinkContextVersion)
            ON CONFLICT(Url) DO UPDATE SET
                LastCrawled = excluded.LastCrawled,
                StatusCode = excluded.StatusCode,
                ETag = excluded.ETag,
                LastModified = excluded.LastModified,
                Title = excluded.Title,
                ContentHash = excluded.ContentHash,
                DocKind = excluded.DocKind,
                LinkContextVersion = excluded.LinkContextVersion;";

        command.Parameters.AddWithValue("@Url", url);
        command.Parameters.AddWithValue("@LastCrawled", DateTime.UtcNow);
        command.Parameters.AddWithValue("@StatusCode", statusCode);
        command.Parameters.AddWithValue("@ETag", eTag ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@LastModified", lastModified ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@Title", title ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@ContentHash", contentHash ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@DocKind", (int)docKind);
        command.Parameters.AddWithValue("@LinkContextVersion", CurrentLinkContextVersion);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Marks a parsed HTML page as having refreshed link-context extraction without changing its
    /// content metadata. Used when the visible text hash is unchanged but hrefs or context may differ.
    /// </summary>
    public static async Task MarkLinkContextCurrentAsync(
        SqliteConnection connection,
        string url,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            UPDATE CrawlState
            SET LinkContextVersion = @Version
            WHERE Url = @Url";
        command.Parameters.AddWithValue("@Version", CurrentLinkContextVersion);
        command.Parameters.AddWithValue("@Url", url);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Records that a URL was visited, updating timestamp and status code, and optionally clears stored metadata.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The URL of the page visited.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="clearMetadata"><c>true</c> to reset headers and content hash (e.g. for redirects/deletions); otherwise, <c>false</c>.</param>
    /// <param name="transaction">An open transaction to enlist this write in, or <c>null</c> to run it standalone.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task RecordVisitAsync(SqliteConnection connection, string url, int statusCode, bool clearMetadata, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Stores every link discovered on a page — in-scope outlinks and off-site links alike —
    /// replacing any existing links for that page. New rows start
    /// <see cref="Reporting.LinkStatus.Unknown"/> with no <c>LastUpdated</c>; their status is
    /// filled in when the destination is visited this run, or by the end-of-crawl verification pass.
    /// When <paramref name="transaction"/> is supplied the delete+insert enlist in it and the caller
    /// commits; otherwise they run as their own atomic transaction.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="fromUrl">The source page URL.</param>
    /// <param name="inScopeLinks">Target URLs within the crawl scope (stored with <c>External=0</c>).</param>
    /// <param name="offsiteLinks">Off-site target URLs (stored with <c>External=1</c>).</param>
    /// <param name="transaction">An open transaction to enlist these writes in, or <c>null</c> to run them as a standalone transaction.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static Task StoreLinksAsync(
        SqliteConnection connection,
        string fromUrl,
        IReadOnlyCollection<string> inScopeLinks,
        IReadOnlyCollection<string> offsiteLinks,
        SqliteTransaction? transaction = null) =>
        StoreLinksAsync(
            connection, fromUrl, inScopeLinks, offsiteLinks,
            Array.Empty<LinkEvidence>(), sourceTitle: null, transaction: transaction);

    /// <summary>
    /// Stores the page's graph edges and compact editorial text for its in-scope links, replacing
    /// the source page's prior rows atomically. Link text is mirrored into FTS5 by table triggers.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="fromUrl">The source page URL.</param>
    /// <param name="inScopeLinks">Target URLs within the crawl scope.</param>
    /// <param name="offsiteLinks">Off-site target URLs retained for verification.</param>
    /// <param name="linkEvidence">Anchor and nearby editorial text for in-scope targets.</param>
    /// <param name="sourceTitle">The referring page's title, used as a weak inbound text field.</param>
    /// <param name="transaction">An open transaction, or <c>null</c> for a standalone transaction.</param>
    public static async Task StoreLinksAsync(
        SqliteConnection connection,
        string fromUrl,
        IReadOnlyCollection<string> inScopeLinks,
        IReadOnlyCollection<string> offsiteLinks,
        IReadOnlyCollection<LinkEvidence> linkEvidence,
        string? sourceTitle,
        SqliteTransaction? transaction = null)
    {
        bool ownTransaction = transaction is null;
        transaction ??= (SqliteTransaction)await connection.BeginTransactionAsync();
        try
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM LinkIndex WHERE FromUrl = @From";
                delete.Parameters.AddWithValue("@From", fromUrl);
                await delete.ExecuteNonQueryAsync();
            }

            using (var deleteContexts = connection.CreateCommand())
            {
                deleteContexts.Transaction = transaction;
                deleteContexts.CommandText = "DELETE FROM LinkContexts WHERE FromUrl = @From";
                deleteContexts.Parameters.AddWithValue("@From", fromUrl);
                await deleteContexts.ExecuteNonQueryAsync();
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
                    await insert.ExecuteNonQueryAsync();
                }

                externalParam.Value = 1;
                foreach (var to in offsiteLinks)
                {
                    toParam.Value = to;
                    await insert.ExecuteNonQueryAsync();
                }
            }

            if (linkEvidence.Count > 0 && inScopeLinks.Count > 0)
            {
                var allowedTargets = inScopeLinks.ToHashSet(StringComparer.OrdinalIgnoreCase);
                using var insertContext = connection.CreateCommand();
                insertContext.Transaction = transaction;
                insertContext.CommandText = @"
                    INSERT OR IGNORE INTO LinkContexts
                        (FromUrl, ToUrl, AnchorText, ContextText, SectionHeading, SourceTitle)
                    VALUES
                        (@From, @To, @Anchor, @Context, @Heading, @SourceTitle)";
                var fromParam = insertContext.Parameters.Add("@From", SqliteType.Text);
                var toParam = insertContext.Parameters.Add("@To", SqliteType.Text);
                var anchorParam = insertContext.Parameters.Add("@Anchor", SqliteType.Text);
                var contextParam = insertContext.Parameters.Add("@Context", SqliteType.Text);
                var headingParam = insertContext.Parameters.Add("@Heading", SqliteType.Text);
                var sourceTitleParam = insertContext.Parameters.Add("@SourceTitle", SqliteType.Text);
                fromParam.Value = fromUrl;
                sourceTitleParam.Value = sourceTitle ?? string.Empty;

                foreach (var evidence in linkEvidence)
                {
                    if (!allowedTargets.Contains(evidence.ToUrl)) continue;
                    toParam.Value = evidence.ToUrl;
                    anchorParam.Value = evidence.AnchorText;
                    contextParam.Value = evidence.ContextText;
                    headingParam.Value = evidence.SectionHeading;
                    await insertContext.ExecuteNonQueryAsync();
                }
            }

            if (ownTransaction)
            {
                await transaction.CommitAsync();
            }
        }
        finally
        {
            if (ownTransaction)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Deletes all stored links (in-scope and off-site) originating from the specified source URL.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="fromUrl">The source page URL.</param>
    /// <param name="transaction">An open transaction to enlist this write in, or <c>null</c> to run it standalone.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task DeleteLinksAsync(SqliteConnection connection, string fromUrl, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            DELETE FROM LinkContexts WHERE FromUrl = @From;
            DELETE FROM LinkIndex WHERE FromUrl = @From;";
        command.Parameters.AddWithValue("@From", fromUrl);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Retrieves the in-scope outlinks stored for the specified page, used to re-derive the frontier
    /// when a page is unchanged (off-site links are excluded — they are never crawled).
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="url">The source page URL.</param>
    /// <returns>A list of in-scope outlink URL strings.</returns>
    public static async Task<List<string>> GetStoredOutlinksAsync(SqliteConnection connection, string url)
    {
        var links = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ToUrl FROM LinkIndex WHERE FromUrl = @From AND External = 0";
        command.Parameters.AddWithValue("@From", url);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
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
    /// <param name="transaction">An open transaction to enlist this write in, or <c>null</c> to run it standalone.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task UpdateLinkStatusByDestinationAsync(SqliteConnection connection, string toUrl, int status, int statusCode, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE LinkIndex SET Status = @Status, StatusCode = @StatusCode, LastUpdated = @Now WHERE ToUrl = @To";
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@StatusCode", statusCode);
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow);
        command.Parameters.AddWithValue("@To", toUrl);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Lists links not determined this run — those whose <c>LastUpdated</c> predates the crawl start
    /// or is unset — so the end-of-crawl pass can verify them. Spans the whole table; the caller
    /// narrows to the links found on pages in the current crawl's scope.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="crawlStartUtc">The crawl start; rows last updated before it are returned.</param>
    /// <returns>The (origin, destination, external) tuples awaiting verification.</returns>
    public static async Task<List<(string FromUrl, string ToUrl, bool External)>> GetLinksToVerifyAsync(SqliteConnection connection, DateTime crawlStartUtc)
    {
        var rows = new List<(string, string, bool)>();
        using var command = connection.CreateCommand();
        // LastUpdated is stored as sortable ISO-8601 text, so the comparison below is sound.
        command.CommandText = "SELECT FromUrl, ToUrl, External FROM LinkIndex WHERE LastUpdated IS NULL OR LastUpdated < @Cutoff";
        command.Parameters.AddWithValue("@Cutoff", crawlStartUtc);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
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
    /// <returns>The (origin, destination, external, status, statusCode) tuples to report.</returns>
    public static async Task<List<(string FromUrl, string ToUrl, bool External, int Status, int StatusCode)>> GetReportableLinksAsync(SqliteConnection connection, DateTime crawlStartUtc)
    {
        var rows = new List<(string, string, bool, int, int)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FromUrl, ToUrl, External, Status, StatusCode FROM LinkIndex WHERE Status IN (2, 3) AND LastUpdated >= @Cutoff";
        command.Parameters.AddWithValue("@Cutoff", crawlStartUtc);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
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
    /// <returns>The list of URLs not visited since the cutoff.</returns>
    public static async Task<List<string>> GetUrlsNotCrawledSinceAsync(SqliteConnection connection, DateTime cutoffUtc)
    {
        var urls = new List<string>();
        using var command = connection.CreateCommand();
        // LastCrawled is stored as sortable ISO-8601 text, so the comparison below is sound.
        command.CommandText = "SELECT Url FROM CrawlState WHERE LastCrawled IS NULL OR LastCrawled < @Cutoff";
        command.Parameters.AddWithValue("@Cutoff", cutoffUtc);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
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
    /// <returns>The list of crawl-state URLs starting with the prefix.</returns>
    public static async Task<List<string>> GetCrawledUrlsWithPrefixAsync(SqliteConnection connection, string urlPrefix)
    {
        var urls = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Url FROM CrawlState WHERE Url LIKE @Pattern ESCAPE '\\'";
        command.Parameters.AddWithValue("@Pattern", EscapeLike(urlPrefix) + "%");
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
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
    /// <param name="transaction">An open transaction to enlist this write in, or <c>null</c> to run it standalone.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task DeleteCrawlStateAsync(SqliteConnection connection, string url, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM CrawlState WHERE Url = @Url";
        command.Parameters.AddWithValue("@Url", url);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Counts what is currently stored: distinct indexed URLs (those with text chunks) and total
    /// crawl-state rows. Used for the end-of-run statistics.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <returns>A tuple of the distinct indexed URL count and the crawl-state row count.</returns>
    public static async Task<(long IndexedUrls, long CrawlStateRows)> GetCountsAsync(SqliteConnection connection)
    {
        long indexedUrls;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(DISTINCT Url) FROM text_chunks";
            indexedUrls = Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
        }

        long crawlStateRows;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM CrawlState";
            crawlStateRows = Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
        }

        return (indexedUrls, crawlStateRows);
    }

    /// <summary>
    /// Recomputes query-independent authority over the indexed internal-link graph using PageRank.
    /// Only URLs with stored text chunks are graph nodes, and only internal edges whose source and
    /// target are both nodes vote. Raw PageRank and a log-normalized [0,1] feature are persisted on
    /// <c>CrawlState</c> in one transaction.
    /// </summary>
    /// <param name="connection">The open writable database connection.</param>
    /// <param name="damping">The probability of following a graph edge rather than teleporting.</param>
    /// <param name="maximumIterations">The iteration ceiling for convergence.</param>
    /// <param name="tolerance">The L1 score-delta convergence threshold.</param>
    /// <returns>Counts of graph nodes, retained edges, and iterations performed.</returns>
    public static async Task<(int NodeCount, int EdgeCount, int Iterations)> RecomputePageRankAsync(
        SqliteConnection connection,
        double damping = 0.85,
        int maximumIterations = 100,
        double tolerance = 1e-8)
    {
        if (damping is < 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(damping));
        if (maximumIterations <= 0) throw new ArgumentOutOfRangeException(nameof(maximumIterations));
        if (tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));

        var nodes = new List<string>();
        using (var nodeCommand = connection.CreateCommand())
        {
            nodeCommand.CommandText = @"
                SELECT DISTINCT cs.Url
                FROM CrawlState cs
                JOIN text_chunks tc ON tc.Url = cs.Url
                ORDER BY cs.Url";
            using var reader = await nodeCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync()) nodes.Add(reader.GetString(0));
        }

        var nodeIndices = new Dictionary<string, int>(nodes.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < nodes.Count; i++) nodeIndices[nodes[i]] = i;

        var outgoing = new List<int>?[nodes.Count];
        int edgeCount = 0;
        using (var edgeCommand = connection.CreateCommand())
        {
            edgeCommand.CommandText = "SELECT FromUrl, ToUrl FROM LinkIndex WHERE External = 0";
            using var reader = await edgeCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!nodeIndices.TryGetValue(reader.GetString(0), out int from)) continue;
                if (!nodeIndices.TryGetValue(reader.GetString(1), out int to)) continue;
                if (from == to) continue; // a page does not endorse itself
                (outgoing[from] ??= new List<int>()).Add(to);
                edgeCount++;
            }
        }

        double[] ranks = nodes.Count == 0
            ? Array.Empty<double>()
            : Enumerable.Repeat(1.0 / nodes.Count, nodes.Count).ToArray();
        int iterations = 0;

        for (; iterations < maximumIterations && nodes.Count > 0; iterations++)
        {
            double danglingMass = 0;
            for (int i = 0; i < ranks.Length; i++)
            {
                if (outgoing[i] is not { Count: > 0 }) danglingMass += ranks[i];
            }

            double baseScore = ((1.0 - damping) + (damping * danglingMass)) / nodes.Count;
            var next = Enumerable.Repeat(baseScore, nodes.Count).ToArray();
            for (int from = 0; from < outgoing.Length; from++)
            {
                if (outgoing[from] is not { Count: > 0 } targets) continue;
                double vote = damping * ranks[from] / targets.Count;
                foreach (int to in targets) next[to] += vote;
            }

            double delta = 0;
            for (int i = 0; i < ranks.Length; i++) delta += Math.Abs(next[i] - ranks[i]);
            ranks = next;
            if (delta <= tolerance)
            {
                iterations++;
                break;
            }
        }

        var authorities = new double[ranks.Length];
        if (ranks.Length > 0)
        {
            // N * PR is 1 for an average page. log1p compresses hubs before min/max scaling,
            // preserving useful distinctions without allowing a single root page to dominate.
            var compressed = ranks.Select(rank => Math.Log(1.0 + (rank * ranks.Length))).ToArray();
            double minimum = compressed.Min();
            double maximum = compressed.Max();
            double range = maximum - minimum;
            if (range > double.Epsilon)
            {
                for (int i = 0; i < authorities.Length; i++)
                {
                    authorities[i] = (compressed[i] - minimum) / range;
                }
            }
        }

        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE CrawlState SET PageRank = 0, Authority = 0";
            await clear.ExecuteNonQueryAsync();
        }

        if (nodes.Count > 0)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = @"
                UPDATE CrawlState
                SET PageRank = @PageRank, Authority = @Authority
                WHERE Url = @Url";
            var pageRankParameter = update.Parameters.Add("@PageRank", SqliteType.Real);
            var authorityParameter = update.Parameters.Add("@Authority", SqliteType.Real);
            var urlParameter = update.Parameters.Add("@Url", SqliteType.Text);

            for (int i = 0; i < nodes.Count; i++)
            {
                pageRankParameter.Value = ranks[i];
                authorityParameter.Value = authorities[i];
                urlParameter.Value = nodes[i];
                await update.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
        return (nodes.Count, edgeCount, iterations);
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
