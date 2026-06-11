namespace LocalSearchEngine.Core;

public class SearchSettings
{
    /// <summary>
    /// How many nearest chunks to pull from the vector index before applying the
    /// similarity threshold. "Close enough" results are by definition among the
    /// nearest, so this only needs to comfortably exceed the number that pass.
    /// </summary>
    public int CandidatePoolSize { get; set; } = 500;

    /// <summary>
    /// Inclusion threshold: a vector hit is returned only if its cosine similarity
    /// (1 - cosine distance) is at least this. Exact keyword matches are always
    /// included regardless. Lower = more (looser) results.
    /// </summary>
    public double MinSimilarity { get; set; } = 0.4;

    /// <summary>Added when the query phrase appears verbatim (an exact FTS5 match).</summary>
    public double ExactPhraseBoost { get; set; } = 0.5;

    /// <summary>
    /// Added when every query term appears in a chunk but not as an adjacent phrase
    /// (a looser FTS5 AND match). Smaller than <see cref="ExactPhraseBoost"/>, and only
    /// applied when there is no exact-phrase hit for the same URL.
    /// </summary>
    public double AndTermsBoost { get; set; } = 0.25;

    /// <summary>Added when the match came from a page heading/title.</summary>
    public double HeadingBoost { get; set; } = 0.3;

    /// <summary>Added when the query appears in the page's &lt;title&gt;.</summary>
    public double TitleBoost { get; set; } = 0.35;

    /// <summary>Added when the URL's file name contains the query.</summary>
    public double FilenameBoost { get; set; } = 0.4;

    /// <summary>Added when the query string appears literally in the result's text.</summary>
    public double TermInTextBoost { get; set; } = 0.2;
}
