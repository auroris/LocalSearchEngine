namespace LocalSearchEngine.Core.Crawling.Engine;

/// <summary>What an <see cref="EmbeddingJob"/> does to a URL's own indexed chunks.</summary>
internal enum ChunkAction
{
    /// <summary>Leave this URL's chunks as they are (an unchanged page).</summary>
    None,
    /// <summary>Remove this URL's chunks (the page is no longer indexed: noindex, gone, a redirect source, or a canonical alias).</summary>
    Delete,
    /// <summary>Replace this URL's chunks: delete the old, then embed <see cref="EmbeddingJob.Text"/>/<see cref="EmbeddingJob.Headings"/> and upsert.</summary>
    Replace
}

/// <summary>
/// One unit of chunk work handed from the crawler to the embedder over the unbounded queue. The crawler
/// has already written the page's crawl-state row; this carries only what the embedder needs to bring
/// <c>text_chunks</c> into line. <see cref="Action"/> covers the URL's own chunks. Mirrors exactly the
/// chunk writes the old single consumer did per job type.
/// </summary>
/// <param name="Url">The page URL whose chunks the action applies to.</param>
/// <param name="Action">What to do with <paramref name="Url"/>'s own chunks.</param>
/// <param name="Text">The main text to embed when <paramref name="Action"/> is <see cref="ChunkAction.Replace"/>.</param>
/// <param name="Headings">The heading text to embed when <paramref name="Action"/> is <see cref="ChunkAction.Replace"/>.</param>
internal sealed record EmbeddingJob(string Url, ChunkAction Action, string Text, string Headings)
{
    /// <summary>
    /// Builds the chunk work implied by a finished <see cref="CrawlJob"/>, or <c>null</c> when the job
    /// touches no chunks at all (an unchanged page) and need not be queued.
    /// </summary>
    /// <param name="job">The classified job the crawler has just applied to crawl-state.</param>
    /// <returns>The matching chunk work, or <c>null</c> if there is nothing for the embedder to do.</returns>
    public static EmbeddingJob? From(CrawlJob job)
    {
        var action = job switch
        {
            IndexJob => ChunkAction.Replace,
            NoIndexJob or GoneJob or AliasJob => ChunkAction.Delete,
            _ => ChunkAction.None, // TouchJob keeps its own chunks.
        };

        // An unchanged page leaves text_chunks untouched — don't bother the queue.
        if (action == ChunkAction.None)
        {
            return null;
        }

        var (text, headings) = job is IndexJob index ? (index.Text, index.Headings) : (string.Empty, string.Empty);
        return new EmbeddingJob(job.Url, action, text, headings);
    }
}
