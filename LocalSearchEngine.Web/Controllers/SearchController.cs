using LocalSearchEngine.Core;
using Microsoft.AspNetCore.Mvc;

namespace LocalSearchEngine.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly VectorSearchService _vectorSearchService;

    public SearchController(VectorSearchService vectorSearchService)
    {
        _vectorSearchService = vectorSearchService;
    }

    [HttpGet("query")]
    public async Task<IActionResult> Query([FromQuery] string q, [FromQuery] double? minScore = null)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("Query is required.");
        }

        var results = await _vectorSearchService.SearchAsync(q, minScore);
        return Ok(results);
    }
}

public class IndexRequest
{
    public string Url { get; set; } = string.Empty;
}
