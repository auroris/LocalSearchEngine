namespace LocalSearchEngine.Core.Searching;

/// <summary>
/// Configures candidate retrieval and the lightweight hybrid re-ranker. Semantic, page-text, and
/// inbound-link candidates are fused by reciprocal rank, then bounded phrase, proximity, field,
/// document, and internal-authority signals refine their order.
/// </summary>
public class SearchSettings
{
    /// <summary>
    /// Gets or sets the maximum number of candidates retrieved by each semantic, broad page-text
    /// BM25, phrase-rescue, and inbound-link pass before candidates are deduplicated by URL and reranked.
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
    /// Gets or sets the reciprocal-rank constant used when fusing semantic, page-text BM25, and
    /// inbound-link BM25 result ranks.
    /// Larger values make rank differences less pronounced. A value around 60 is conventional.
    /// </summary>
    public double ReciprocalRankConstant { get; set; } = 60;

    /// <summary>Gets or sets the weight of the semantic result rank in reciprocal-rank fusion.</summary>
    public double SemanticWeight { get; set; } = 1.0;

    /// <summary>Gets or sets the weight of the BM25 keyword result rank in reciprocal-rank fusion.</summary>
    public double KeywordWeight { get; set; } = 1.0;

    /// <summary>Gets or sets the weight of the inbound anchor/context BM25 rank in reciprocal-rank fusion.</summary>
    public double InboundLinkWeight { get; set; } = 0.75;

    /// <summary>
    /// Gets or sets the maximum bounded bonus for coverage, proximity, and phrase quality in the
    /// best inbound-link description of a target.
    /// </summary>
    public double InboundContextBoost { get; set; } = 0.2;

    /// <summary>
    /// Gets or sets how strongly the referring page's normalized authority may improve an inbound
    /// candidate's BM25 ordering. A value of 0.25 changes its magnitude by at most 25%.
    /// </summary>
    public double InboundSourceAuthorityWeight { get; set; } = 0.25;

    /// <summary>
    /// Gets or sets the maximum query-independent boost from the target page's normalized PageRank.
    /// </summary>
    public double AuthorityWeight { get; set; } = 0.2;

    /// <summary>Gets or sets the bonus for query terms occurring as an adjacent, ordered phrase.</summary>
    public double ExactPhraseBoost { get; set; } = 0.35;

    /// <summary>
    /// Gets or sets the maximum bonus for query terms occurring close together. Partial term
    /// coverage and wider spans receive a proportionally smaller bonus.
    /// </summary>
    public double ProximityBoost { get; set; } = 0.15;

    /// <summary>
    /// Gets or sets the relevance score boost added when a match occurs in a page heading.
    /// </summary>
    public double HeadingBoost { get; set; } = 0.3;

    /// <summary>
    /// Gets or sets the maximum coverage-weighted boost for query terms in the page's HTML title.
    /// </summary>
    public double TitleBoost { get; set; } = 0.35;

    /// <summary>
    /// Gets or sets the maximum coverage-weighted boost for query terms in the URL's final filename or slug.
    /// </summary>
    public double FilenameBoost { get; set; } = 0.4;

    /// <summary>
    /// Gets or sets the maximum relevance boost for query-term coverage in matching chunks.
    /// A chunk containing only some distinct terms receives a proportional fraction.
    /// </summary>
    public double TermInTextBoost { get; set; } = 0.2;

    /// <summary>
    /// Gets or sets the maximum bonus for corroborating matches in multiple chunks from one URL.
    /// The ranker applies diminishing returns and never exceeds this amount.
    /// </summary>
    public double MultiChunkBoost { get; set; } = 0.1;

    /// <summary>
    /// Gets or sets the soft penalty applied to PDF and DOCX results. Unlike a hard document-type
    /// tier, sufficiently relevant documents can still outrank weaker HTML pages.
    /// </summary>
    public double NonHtmlPenalty { get; set; } = 0.15;
}
