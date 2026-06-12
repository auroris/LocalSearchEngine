namespace LocalSearchEngine.Core.Searching;

/// <summary>
/// Configures relevance tuning settings for search queries, including candidate pools, thresholds, and keyword boosts.
/// </summary>
public class SearchSettings
{
    /// <summary>
    /// Gets or sets the number of nearest candidate chunks retrieved from the vector index before
    /// they are filtered by <see cref="MaxDistance"/> and ranked.
    /// </summary>
    public int CandidatePoolSize { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum cosine distance a vector hit may have from the query embedding to
    /// be kept — lower is stricter, higher is more permissive. Distance comes straight from the
    /// vector connector: it ranges from 0 (chunk points the same direction as the query) through 1
    /// (orthogonal/unrelated) to a theoretical 2 (opposite), so cosine similarity is 1 - distance.
    /// Hits farther than this are dropped before ranking. The default 0.6 keeps anything with
    /// cosine similarity of at least ~0.4.
    /// </summary>
    public double MaxDistance { get; set; } = 0.6;

    /// <summary>
    /// Gets or sets the relevance score boost added when the query phrase matches a verbatim full-text search phrase.
    /// </summary>
    public double ExactPhraseBoost { get; set; } = 0.5;

    /// <summary>
    /// Gets or sets the relevance score boost added when all terms in the query appear in a chunk, but not as a verbatim phrase.
    /// </summary>
    public double AndTermsBoost { get; set; } = 0.25;

    /// <summary>
    /// Gets or sets the relevance score boost added when a match occurs in a page heading.
    /// </summary>
    public double HeadingBoost { get; set; } = 0.3;

    /// <summary>
    /// Gets or sets the relevance score boost added when a query match is found within the page's HTML title.
    /// </summary>
    public double TitleBoost { get; set; } = 0.35;

    /// <summary>
    /// Gets or sets the relevance score boost added when the URL's file name contains the query terms.
    /// </summary>
    public double FilenameBoost { get; set; } = 0.4;

    /// <summary>
    /// Gets or sets the relevance score boost added when the literal query string is present in the text snippet.
    /// </summary>
    public double TermInTextBoost { get; set; } = 0.2;
}
