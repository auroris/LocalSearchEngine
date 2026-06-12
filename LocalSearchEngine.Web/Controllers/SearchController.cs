using LocalSearchEngine.Core.Searching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace LocalSearchEngine.Controllers;

/// <summary>
/// Provides HTTP API endpoints for executing search queries against the vector search engine.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly VectorSearchService _vectorSearchService;
    private readonly ILogger<SearchController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchController"/> class.
    /// </summary>
    /// <param name="vectorSearchService">The vector search service provider.</param>
    /// <param name="logger">The logger instance.</param>
    public SearchController(VectorSearchService vectorSearchService, ILogger<SearchController> logger)
    {
        _vectorSearchService = vectorSearchService;
        _logger = logger;
    }

    /// <summary>
    /// Executes a hybrid vector and keyword search for the specified query string.
    /// </summary>
    /// <param name="q">The search query text.</param>
    /// <returns>An HTTP result containing search response hits, or service unavailable status if the database index is missing.</returns>
    [HttpGet("query")]
    public async Task<IActionResult> Query([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("Query is required.");
        }

        try
        {
            var results = await _vectorSearchService.SearchAsync(q);
            return Ok(results);
        }
        catch (SqliteException ex)
        {
            // A missing/locked index surfaces as a SQLite error (e.g. the file or tables don't
            // exist yet because the crawler hasn't run). Keep the friendly "build the index" hint.
            _logger.LogError(ex, "Search index unavailable for query {Query}", q);
            return StatusCode(503, new { error = "Search index is not available. Run the crawler to build the index first." });
        }
        catch (Exception ex)
        {
            // Anything else is a genuine bug, not a missing index — don't disguise it.
            _logger.LogError(ex, "Search failed for query {Query}", q);
            return StatusCode(500, new { error = "An unexpected error occurred while searching." });
        }
    }
}
