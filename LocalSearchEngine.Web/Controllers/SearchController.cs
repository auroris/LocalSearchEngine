using LocalSearchEngine.Core;
using Microsoft.AspNetCore.Mvc;

namespace LocalSearchEngine.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly VectorSearchService _vectorSearchService;
    private readonly ILogger<SearchController> _logger;

    public SearchController(VectorSearchService vectorSearchService, ILogger<SearchController> logger)
    {
        _vectorSearchService = vectorSearchService;
        _logger = logger;
    }

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for query {Query}", q);
            return StatusCode(503, new { error = "Search index is not available. Run the crawler to build the index first." });
        }
    }
}
