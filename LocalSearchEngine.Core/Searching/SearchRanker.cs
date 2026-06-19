using LocalSearchEngine.Core.Crawling.Policies;

namespace LocalSearchEngine.Core.Searching;

/// <summary>
/// Represents a semantic (vector) match candidate containing information about the chunk and its cosine distance.
/// </summary>
/// <param name="Url">The URL of the page containing the chunk.</param>
/// <param name="Text">The text content of the chunk.</param>
/// <param name="IsHeading">A value indicating whether the chunk represents a heading.</param>
/// <param name="Distance">The cosine distance between the query and the chunk embedding.</param>
public readonly record struct VectorCandidate(string Url, string Text, bool IsHeading, double Distance);

/// <summary>
/// Represents a keyword (full-text search) match candidate.
/// </summary>
/// <param name="Url">The URL of the page containing the match.</param>
/// <param name="Text">The text content of the matching chunk.</param>
/// <param name="IsHeading">A value indicating whether the match came from a heading.</param>
/// <param name="ExactPhrase">A value indicating whether the match is an exact phrase match.</param>
public readonly record struct KeywordCandidate(string Url, string Text, bool IsHeading, bool ExactPhrase = true);

/// <summary>
/// Collapses the two candidate streams of a hybrid search — semantic vector hits and keyword (FTS5)
/// hits — into a single ranked result list. Candidates are aggregated by URL, then ordered in three
/// tiers: web pages rank ahead of every PDF/DOCX; within a doc-type group a verbatim-phrase keyword
/// hit ranks ahead of a looser all-terms or semantic-only hit; and within a group the configured
/// fine-grained boosts (heading, title, filename, literal-term-in-snippet) layered on the semantic
/// similarity break the tie. The first two are hard tiers — no fine score lifts a PDF above a web
/// page, or an all-terms hit above a phrase hit in the same group. Pure and stateless — the same
/// candidates always rank the same way.
/// </summary>
public static class SearchRanker
{
    /// <summary>
    /// Ranks semantic and keyword candidates based on query similarity and search settings configuration.
    /// </summary>
    /// <param name="vectorHits">The collection of semantic vector hits.</param>
    /// <param name="keywordHits">The collection of keyword full-text search hits.</param>
    /// <param name="query">The raw query string.</param>
    /// <param name="settings">The search relevance settings.</param>
    /// <param name="titles">Optional dictionary map of page URLs to titles.</param>
    /// <param name="docKinds">Optional dictionary map of page URLs to their <see cref="DocKind"/>; URLs absent from it are treated as web pages.</param>
    /// <returns>A sorted list of ranked search result items.</returns>
    public static List<SearchResultItem> Rank(
        IEnumerable<VectorCandidate> vectorHits,
        IEnumerable<KeywordCandidate> keywordHits,
        string query,
        SearchSettings settings,
        IReadOnlyDictionary<string, string?>? titles = null,
        IReadOnlyDictionary<string, DocKind>? docKinds = null)
    {
        var byUrl = new Dictionary<string, Aggregate>(StringComparer.OrdinalIgnoreCase);

        // Semantic pass: keep only chunks within the cosine-distance ceiling ("close enough").
        foreach (var hit in vectorHits)
        {
            // hit.Distance is cosine distance from the connector: 0 = same direction as the query,
            // larger = less similar. Drop anything farther than the configured ceiling, then turn
            // the surviving distance into a base relevance score (higher = closer).
            if (hit.Distance > settings.MaxDistance) continue;
            double similarity = 1.0 - hit.Distance;

            var agg = GetOrCreate(byUrl, hit.Url);
            if (similarity > agg.Similarity || agg.Text.Length == 0)
            {
                agg.Similarity = Math.Max(agg.Similarity, similarity);
                agg.Text = hit.Text; // representative snippet = most similar chunk
            }
            agg.MatchedHeading |= hit.IsHeading;
        }

        // Keyword pass: keyword matches are always relevant, threshold or not. Only a verbatim
        // phrase hit promotes the URL into the exact-match tier; a looser all-terms hit leaves it
        // in the same tier as a semantic-only hit, ordered by the fine score below.
        foreach (var hit in keywordHits)
        {
            var agg = GetOrCreate(byUrl, hit.Url);
            if (hit.ExactPhrase) agg.ExactPhrase = true;
            agg.MatchedHeading |= hit.IsHeading;
            if (agg.Text.Length == 0) agg.Text = hit.Text; // only if no vector snippet chosen
        }

        // Final pass: resolve each URL's title and doc kind, then compute the fine within-group
        // score — the semantic similarity plus the configured term boosts (heading, filename,
        // literal-term-in-snippet, title). The web-page and exact-phrase tiers are applied by the
        // sort below, not folded into this score.
        foreach (var agg in byUrl.Values)
        {
            string? title = null;
            titles?.TryGetValue(agg.Url, out title);
            agg.Title = title;

            // Missing/NULL doc kind ⇒ treat as a web page: only PDFs/DOCX are ever demoted, and they
            // always have their kind recorded at index time, so a gap here means an HTML page.
            DocKind kind = DocKind.Html;
            if (docKinds != null && docKinds.TryGetValue(agg.Url, out var k)) kind = k;
            agg.Kind = kind;

            double score = agg.Similarity;
            if (agg.MatchedHeading) score += settings.HeadingBoost;
            if (UrlFileNameContains(agg.Url, query)) score += settings.FilenameBoost;
            if (agg.Text.Contains(query, StringComparison.OrdinalIgnoreCase)) score += settings.TermInTextBoost;
            if (!string.IsNullOrEmpty(title) && title.Contains(query, StringComparison.OrdinalIgnoreCase)) score += settings.TitleBoost;
            agg.Score = score;
        }

        // Lexicographic ordering: (1) web pages ahead of PDFs/DOCX, (2) exact-phrase hits ahead of
        // the rest, (3) the fine within-group score. OrderByDescending on a bool puts true first.
        return byUrl.Values
            .OrderByDescending(a => a.Kind == DocKind.Html)
            .ThenByDescending(a => a.ExactPhrase)
            .ThenByDescending(a => a.Score)
            .Select(a => new SearchResultItem
            {
                Url = a.Url,
                Title = a.Title,
                Text = a.Text,
                Similarity = a.Similarity,
                Score = a.Score,
                DocKind = a.Kind
            })
            .ToList();
    }

    /// <summary>
    /// Retrieves or creates an aggregation record for a specific URL in the ranking pass.
    /// </summary>
    /// <param name="byUrl">The current aggregation dictionary map.</param>
    /// <param name="url">The URL key.</param>
    /// <returns>The <see cref="Aggregate"/> record.</returns>
    private static Aggregate GetOrCreate(Dictionary<string, Aggregate> byUrl, string url)
    {
        if (!byUrl.TryGetValue(url, out var agg))
        {
            agg = new Aggregate { Url = url };
            byUrl[url] = agg;
        }
        return agg;
    }

    /// <summary>
    /// Determines whether the URL filename contains query keywords.
    /// </summary>
    /// <param name="url">The URL string.</param>
    /// <param name="query">The query string.</param>
    /// <returns><c>true</c> if the filename slug matches the query keywords; otherwise, <c>false</c>.</returns>
    private static bool UrlFileNameContains(string url, string query)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var segment = uri.Segments.LastOrDefault()?.TrimEnd('/');
        if (string.IsNullOrEmpty(segment)) return false;

        // Slugs use '-'/'_' where a query uses spaces, so "user guide" should match
        // "user-guide.html". Decode percent-escapes and fold both separators to spaces
        // before comparing.
        var fileName = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(segment));
        return Slugify(fileName).Contains(Slugify(query), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes dashes and underscores to spaces to fold slugs into plain text.
    /// </summary>
    /// <param name="value">The string to slugify.</param>
    /// <returns>The cleaned string with space replacements.</returns>
    private static string Slugify(string value) => value.Replace('-', ' ').Replace('_', ' ');

    /// <summary>
    /// Holds aggregated matching data for a single URL during the ranking pass.
    /// </summary>
    private sealed class Aggregate
    {
        /// <summary>The page URL.</summary>
        public string Url = string.Empty;
        /// <summary>The resolved page title, if known (filled in the final pass).</summary>
        public string? Title;
        /// <summary>The representative text snippet.</summary>
        public string Text = string.Empty;
        /// <summary>The highest semantic similarity score.</summary>
        public double Similarity;
        /// <summary>Whether an exact phrase hit was matched (promotes the URL into the exact-match tier).</summary>
        public bool ExactPhrase;
        /// <summary>Whether a heading chunk was matched.</summary>
        public bool MatchedHeading;
        /// <summary>The classified document kind, resolved in the final pass.</summary>
        public DocKind Kind;
        /// <summary>The fine within-group relevance score, computed in the final pass.</summary>
        public double Score;
    }
}

/// <summary>
/// Represents a single ranked search result item.
/// </summary>
public class SearchResultItem
{
    /// <summary>Gets or sets the URL of the result page.</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>Gets or sets the title of the result page, if known.</summary>
    public string? Title { get; set; }
    /// <summary>Gets or sets the matching text snippet or chunk content.</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>Gets or sets the highest cosine similarity score.</summary>
    public double Similarity { get; set; }
    /// <summary>Gets or sets the fine within-group relevance score. Overall ordering also applies the web-page and exact-phrase tiers ahead of this score.</summary>
    public double Score { get; set; }
    /// <summary>Gets or sets the document kind (web page, PDF, or DOCX) of the result.</summary>
    public DocKind DocKind { get; set; }
}

/// <summary>
/// Represents the query response containing ranked search results.
/// </summary>
public class SearchResponse
{
    /// <summary>Gets or sets the list of ranked search result items.</summary>
    public List<SearchResultItem> Items { get; set; } = new();
    /// <summary>Gets or sets the total number of match items found.</summary>
    public int TotalMatches { get; set; }
}
