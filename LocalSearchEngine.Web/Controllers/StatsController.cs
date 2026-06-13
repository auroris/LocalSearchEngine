using System.Globalization;
using LocalSearchEngine.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace LocalSearchEngine.Controllers;

/// <summary>
/// Provides an HTTP API endpoint summarizing the search index: how much is indexed and how fresh it is.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly DatabaseConfig _dbConfig;
    private readonly ILogger<StatsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatsController"/> class.
    /// </summary>
    /// <param name="dbConfig">The configuration containing database connection details.</param>
    /// <param name="logger">The logger instance.</param>
    public StatsController(DatabaseConfig dbConfig, ILogger<StatsController> logger)
    {
        _dbConfig = dbConfig;
        _logger = logger;
    }

    /// <summary>
    /// Returns index statistics: indexed page count, chunk count, tracked URL count,
    /// database size, and the most recent crawl timestamp.
    /// </summary>
    /// <returns>An HTTP result with the statistics, or service unavailable if the index doesn't exist yet.</returns>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            using var connection = new SqliteConnection(_dbConfig.ConnectionString);
            await connection.OpenAsync();

            long indexedPages = await ScalarLongAsync(connection, "SELECT COUNT(DISTINCT Url) FROM text_chunks");
            long totalChunks = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM text_chunks");
            long trackedUrls = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM CrawlState");
            // page_count × page_size reflects the real database size even mid-crawl, when a
            // plain file-length check would be confused by content still sitting in the WAL.
            long dbSizeBytes = await ScalarLongAsync(connection, "PRAGMA page_count")
                             * await ScalarLongAsync(connection, "PRAGMA page_size");

            DateTime? lastCrawledUtc = null;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT MAX(LastCrawled) FROM CrawlState";
                if (await command.ExecuteScalarAsync() is string raw
                    && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    lastCrawledUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                }
            }

            return Ok(new
            {
                indexedPages,
                totalChunks,
                trackedUrls,
                dbSizeBytes,
                lastCrawledUtc,
            });
        }
        catch (SqliteException ex)
        {
            // Same situation as a search against a missing index: the crawler hasn't run yet.
            _logger.LogError(ex, "Index statistics unavailable.");
            return StatusCode(503, new { error = "Search index is not available. Run the crawler to build the index first." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute index statistics.");
            return StatusCode(500, new { error = "An unexpected error occurred while reading index statistics." });
        }
    }

    /// <summary>
    /// Executes a query returning a single integer/long value.
    /// </summary>
    /// <param name="connection">The open database connection.</param>
    /// <param name="sql">The query to execute.</param>
    /// <returns>The long value, or 0 if the query returned null.</returns>
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() is long value ? value : 0L;
    }
}
