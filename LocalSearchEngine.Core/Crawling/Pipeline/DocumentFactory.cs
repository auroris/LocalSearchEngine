using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Crawling.Pipeline;

/// <summary>
/// Resolves an unknown <see cref="PageDocument"/> into the typed document that can process its body,
/// after the single fetch. Placed here rather than in a constructor because an object cannot decide
/// mid-construction to be a different type — resolution has to be a function that returns the right
/// one. Classification defers to <see cref="CrawlPolicy.ClassifyContent"/>: the Content-Type header
/// wins, magic bytes are the fallback for misdeclared servers.
/// </summary>
internal static class DocumentFactory
{
    /// <summary>
    /// Picks the typed document for a fetched body.
    /// </summary>
    /// <param name="page">The unresolved work item.</param>
    /// <param name="fetch">Its successful download.</param>
    /// <returns>The typed document to process, or <c>null</c> when the content kind is unsupported
    /// (the worker records a skip and a plain visit).</returns>
    public static Document? Resolve(PageDocument page, FetchResult fetch) =>
        CrawlPolicy.ClassifyContent(fetch.ContentType, fetch.Body) switch
        {
            DocKind.Html => new HtmlDocument(page),
            DocKind.Pdf => new PdfDocument(page),
            DocKind.Docx => new DocxDocument(page),
            _ => null,
        };
}
