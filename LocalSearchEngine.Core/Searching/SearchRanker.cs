using System.Text.RegularExpressions;

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
/// Represents a keyword match from SQLite FTS5, ordered by BM25 relevance within its retrieval pass.
/// </summary>
/// <param name="Url">The URL of the page containing the match.</param>
/// <param name="Text">The text content of the matching chunk.</param>
/// <param name="IsHeading">A value indicating whether the match came from a heading.</param>
/// <param name="ExactPhrase">A value indicating whether FTS5 matched the query terms as an adjacent phrase.</param>
/// <param name="Bm25">The raw FTS5 BM25 value. Smaller values are more relevant.</param>
public readonly record struct KeywordCandidate(
    string Url,
    string Text,
    bool IsHeading,
    bool ExactPhrase = false,
    double Bm25 = 0);

/// <summary>
/// Represents a target described by anchor text and nearby content on another crawled page.
/// </summary>
/// <param name="Url">The in-scope target URL being endorsed.</param>
/// <param name="Text">The compact anchor, context, section-heading, and source-title text.</param>
/// <param name="Bm25">The field-weighted FTS5 BM25 value; smaller values are more relevant.</param>
/// <param name="SourceAuthority">The normalized PageRank of the referring page.</param>
public readonly record struct InboundLinkCandidate(
    string Url,
    string Text,
    double Bm25,
    double SourceAuthority);

/// <summary>
/// Collapses semantic and BM25 candidate streams into a URL-level result list. Each stream is first
/// reduced to its best rank per URL, then combined with normalized reciprocal-rank fusion. Bounded
/// lexical features refine that fused score: term coverage, phrase adjacency, proximity, matches in
/// headings/titles/filenames, corroborating chunks, inbound anchor context, internal PageRank, and
/// a soft non-HTML penalty. This keeps the re-ranker inexpensive and avoids adding incompatible raw
/// cosine-distance and BM25 scales.
/// </summary>
public static class SearchRanker
{
    private static readonly Regex WordToken = new(
        @"[\p{L}\p{Nd}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Ranks semantic and keyword candidates based on query similarity and search settings configuration.
    /// </summary>
    /// <param name="vectorHits">Semantic hits in nearest-neighbour order.</param>
    /// <param name="keywordHits">BM25-scored keyword hits from the broad and phrase-rescue passes.</param>
    /// <param name="query">The query text after search operators have been removed.</param>
    /// <param name="settings">The search relevance settings.</param>
    /// <param name="titles">Optional dictionary map of page URLs to titles.</param>
    /// <param name="docKinds">Optional dictionary map of page URLs to their <see cref="DocKind"/>; URLs absent from it are treated as web pages.</param>
    /// <param name="authorities">Optional dictionary map of page URLs to normalized internal PageRank.</param>
    /// <param name="inboundLinkHits">Optional inbound anchor/context candidates with field-weighted BM25 scores.</param>
    /// <returns>A sorted list of ranked search result items.</returns>
    public static List<SearchResultItem> Rank(
        IEnumerable<VectorCandidate> vectorHits,
        IEnumerable<KeywordCandidate> keywordHits,
        string query,
        SearchSettings settings,
        IReadOnlyDictionary<string, string?>? titles = null,
        IReadOnlyDictionary<string, DocKind>? docKinds = null,
        IReadOnlyDictionary<string, double>? authorities = null,
        IEnumerable<InboundLinkCandidate>? inboundLinkHits = null)
    {
        ArgumentNullException.ThrowIfNull(vectorHits);
        ArgumentNullException.ThrowIfNull(keywordHits);
        ArgumentNullException.ThrowIfNull(settings);

        var queryTokens = Tokenize(query);
        var byUrl = new Dictionary<string, Aggregate>(StringComparer.OrdinalIgnoreCase);

        // The vector connector returns chunks, but reciprocal-rank fusion is performed at the URL
        // level. Repeated chunks from one page therefore consume one semantic rank, not many.
        int nextVectorRank = 0;
        var vectorRankedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in vectorHits.OrderBy(candidate => candidate.Distance))
        {
            if (hit.Distance > settings.MaxDistance) continue;

            var agg = GetOrCreate(byUrl, hit.Url);
            if (vectorRankedUrls.Add(hit.Url)) agg.VectorRank = ++nextVectorRank;

            double similarity = 1.0 - hit.Distance;
            if (similarity > agg.Similarity || agg.VectorText.Length == 0)
            {
                agg.Similarity = Math.Max(agg.Similarity, similarity);
                agg.VectorText = hit.Text;
            }

            AddTextEvidence(agg, hit.Text, AnalyzeText(queryTokens, hit.Text));
            agg.MatchedHeading |= hit.IsHeading;
        }

        // Establish URL-level lexical rank from the best BM25-scored chunk across the broad and
        // phrase-rescue passes. Repeated chunks from one URL consume only one lexical rank.
        int nextKeywordRank = 0;
        var keywordRankedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in keywordHits.OrderBy(candidate => candidate.Bm25))
        {
            var agg = GetOrCreate(byUrl, hit.Url);
            if (keywordRankedUrls.Add(hit.Url)) agg.KeywordRank = ++nextKeywordRank;

            var features = AnalyzeText(queryTokens, hit.Text);
            bool phraseMatch = hit.ExactPhrase || features.ExactPhrase;
            agg.ExactPhrase |= phraseMatch;
            agg.MatchedHeading |= hit.IsHeading;
            agg.BestBm25 = Math.Min(agg.BestBm25, hit.Bm25);
            AddTextEvidence(agg, hit.Text, features);

            double snippetQuality = features.Coverage + features.Proximity + (phraseMatch ? 1.0 : 0.0);
            if (snippetQuality > agg.KeywordSnippetQuality || agg.KeywordText.Length == 0)
            {
                agg.KeywordSnippetQuality = snippetQuality;
                agg.KeywordText = hit.Text;
            }
        }

        // Inbound descriptions form an independent lexical retrieval stream. Authority of the
        // referring page changes BM25 magnitude only modestly, so a strong textual endorsement from
        // an ordinary page remains more valuable than a weak mention from a hub.
        int nextInboundRank = 0;
        var inboundRankedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (inboundLinkHits != null)
        {
            foreach (var hit in inboundLinkHits.OrderBy(candidate =>
                         AdjustedInboundBm25(candidate, settings.InboundSourceAuthorityWeight)))
            {
                var agg = GetOrCreate(byUrl, hit.Url);
                if (inboundRankedUrls.Add(hit.Url)) agg.InboundRank = ++nextInboundRank;

                var features = AnalyzeText(queryTokens, hit.Text);
                double quality = (0.60 * features.Coverage)
                    + (0.25 * features.Proximity)
                    + (features.ExactPhrase ? 0.15 : 0);
                if (quality > agg.InboundQuality || agg.InboundText.Length == 0)
                {
                    agg.InboundQuality = quality;
                    agg.InboundText = hit.Text;
                }
            }
        }

        foreach (var agg in byUrl.Values)
        {
            string? title = null;
            titles?.TryGetValue(agg.Url, out title);
            agg.Title = title;

            if (docKinds != null && docKinds.TryGetValue(agg.Url, out var kind)) agg.Kind = kind;
            else agg.Kind = DocKind.Html;
            if (authorities != null && authorities.TryGetValue(agg.Url, out double authority))
            {
                agg.Authority = Math.Clamp(authority, 0, 1);
            }

            var titleFeatures = AnalyzeText(queryTokens, title);
            var filenameFeatures = AnalyzeText(queryTokens, GetUrlFileName(agg.Url));

            double score = 0;
            if (agg.VectorRank > 0)
            {
                score += settings.SemanticWeight * ReciprocalRank(agg.VectorRank, settings.ReciprocalRankConstant);
            }
            if (agg.KeywordRank > 0)
            {
                score += settings.KeywordWeight * ReciprocalRank(agg.KeywordRank, settings.ReciprocalRankConstant);
            }
            if (agg.InboundRank > 0)
            {
                score += settings.InboundLinkWeight * ReciprocalRank(agg.InboundRank, settings.ReciprocalRankConstant);
                score += settings.InboundContextBoost * agg.InboundQuality;
            }

            if (agg.ExactPhrase) score += settings.ExactPhraseBoost;
            score += settings.TermInTextBoost * agg.TextCoverage;
            score += settings.ProximityBoost * agg.Proximity;
            if (agg.MatchedHeading) score += settings.HeadingBoost;
            score += settings.TitleBoost * titleFeatures.Coverage;
            score += settings.FilenameBoost * filenameFeatures.Coverage;
            score += settings.AuthorityWeight * agg.Authority;

            // Each additional distinct matching chunk adds less than the previous one. The bonus
            // approaches, but never exceeds, MultiChunkBoost.
            if (agg.EvidenceChunks.Count > 1)
            {
                score += settings.MultiChunkBoost * (1.0 - (1.0 / agg.EvidenceChunks.Count));
            }

            if (agg.Kind != DocKind.Html) score -= settings.NonHtmlPenalty;
            agg.Score = score;

            // Prefer a direct lexical excerpt when it explains the query at least as well as the
            // best semantic chunk; otherwise retain the semantically closest representative text.
            var vectorFeatures = AnalyzeText(queryTokens, agg.VectorText);
            agg.Text = agg.KeywordText.Length > 0 &&
                (agg.ExactPhrase || agg.KeywordSnippetQuality >= vectorFeatures.Coverage + vectorFeatures.Proximity)
                    ? agg.KeywordText
                    : agg.VectorText;
            if (agg.Text.Length == 0) agg.Text = agg.InboundText;
        }

        return byUrl.Values
            .OrderByDescending(a => a.Score)
            .ThenByDescending(a => a.Similarity)
            .ThenBy(a => a.BestBm25)
            .ThenBy(a => a.Url, StringComparer.OrdinalIgnoreCase)
            .Select(a => new SearchResultItem
            {
                Url = a.Url,
                Title = a.Title,
                Text = a.Text,
                Similarity = a.Similarity,
                Score = a.Score,
                Authority = a.Authority,
                DocKind = a.Kind
            })
            .ToList();
    }

    private static Aggregate GetOrCreate(Dictionary<string, Aggregate> byUrl, string url)
    {
        if (!byUrl.TryGetValue(url, out var agg))
        {
            agg = new Aggregate { Url = url };
            byUrl[url] = agg;
        }
        return agg;
    }

    private static void AddTextEvidence(Aggregate agg, string text, TextFeatures features)
    {
        if (!string.IsNullOrWhiteSpace(text)) agg.EvidenceChunks.Add(text);

        agg.ExactPhrase |= features.ExactPhrase;
        agg.TextCoverage = Math.Max(agg.TextCoverage, features.Coverage);
        agg.Proximity = Math.Max(agg.Proximity, features.Proximity);
    }

    /// <summary>
    /// Normalizes reciprocal rank so the first result contributes exactly 1.0 before weighting.
    /// </summary>
    private static double ReciprocalRank(int rank, double constant)
    {
        double safeConstant = Math.Max(0, constant);
        return (safeConstant + 1.0) / (safeConstant + rank);
    }

    private static double AdjustedInboundBm25(InboundLinkCandidate candidate, double sourceAuthorityWeight)
    {
        double sourceAuthority = Math.Clamp(candidate.SourceAuthority, 0, 1);
        double boundedWeight = Math.Max(0, sourceAuthorityWeight);
        return candidate.Bm25 * (1.0 + (boundedWeight * sourceAuthority));
    }

    private static TextFeatures AnalyzeText(IReadOnlyList<string> queryTokens, string? text)
    {
        if (queryTokens.Count == 0 || string.IsNullOrWhiteSpace(text)) return default;

        var textTokens = Tokenize(text);
        if (textTokens.Count == 0) return default;

        var required = queryTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = textTokens
            .Where(required.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        double coverage = present.Count / (double)required.Count;
        bool exactPhrase = queryTokens.Count > 1 && ContainsSequence(textTokens, queryTokens);

        if (present.Count < 2)
        {
            return new TextFeatures(coverage, exactPhrase, 0);
        }

        // Find the narrowest token window containing every query term that is present in this
        // text. Multiplying density by overall coverage prevents a tight two-term fragment from
        // looking equivalent to a tight match of all five query terms.
        var windowCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int formed = 0;
        int left = 0;
        int minimumSpan = int.MaxValue;

        for (int right = 0; right < textTokens.Count; right++)
        {
            string rightToken = textTokens[right];
            if (present.Contains(rightToken))
            {
                windowCounts.TryGetValue(rightToken, out int count);
                windowCounts[rightToken] = count + 1;
                if (count == 0) formed++;
            }

            while (formed == present.Count && left <= right)
            {
                minimumSpan = Math.Min(minimumSpan, right - left + 1);
                string leftToken = textTokens[left++];
                if (!present.Contains(leftToken)) continue;

                int count = windowCounts[leftToken] - 1;
                windowCounts[leftToken] = count;
                if (count == 0) formed--;
            }
        }

        double density = minimumSpan == int.MaxValue ? 0 : present.Count / (double)minimumSpan;
        return new TextFeatures(coverage, exactPhrase, coverage * density);
    }

    private static bool ContainsSequence(IReadOnlyList<string> textTokens, IReadOnlyList<string> queryTokens)
    {
        if (queryTokens.Count == 0 || queryTokens.Count > textTokens.Count) return false;

        for (int start = 0; start <= textTokens.Count - queryTokens.Count; start++)
        {
            int offset = 0;
            while (offset < queryTokens.Count &&
                   textTokens[start + offset].Equals(queryTokens[offset], StringComparison.OrdinalIgnoreCase))
            {
                offset++;
            }
            if (offset == queryTokens.Count) return true;
        }
        return false;
    }

    private static List<string> Tokenize(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : WordToken.Matches(value)
            .Select(match => match.Value.ToLowerInvariant())
            .ToList();

    private static string GetUrlFileName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
        var segment = uri.Segments.LastOrDefault()?.TrimEnd('/');
        if (string.IsNullOrEmpty(segment)) return string.Empty;
        return Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(segment))
            .Replace('-', ' ')
            .Replace('_', ' ');
    }

    private readonly record struct TextFeatures(double Coverage, bool ExactPhrase, double Proximity);

    private sealed class Aggregate
    {
        public string Url = string.Empty;
        public string? Title;
        public string Text = string.Empty;
        public string VectorText = string.Empty;
        public string KeywordText = string.Empty;
        public double KeywordSnippetQuality;
        public double Similarity;
        public double BestBm25 = double.PositiveInfinity;
        public int VectorRank;
        public int KeywordRank;
        public int InboundRank;
        public bool ExactPhrase;
        public bool MatchedHeading;
        public double TextCoverage;
        public double Proximity;
        public string InboundText = string.Empty;
        public double InboundQuality;
        public double Authority;
        public HashSet<string> EvidenceChunks { get; } = new(StringComparer.Ordinal);
        public DocKind Kind;
        public double Score;
    }
}

/// <summary>Represents a single ranked search result item.</summary>
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
    /// <summary>Gets or sets the final fused and feature-adjusted relevance score.</summary>
    public double Score { get; set; }
    /// <summary>Gets or sets normalized internal PageRank in the range [0,1].</summary>
    public double Authority { get; set; }
    /// <summary>Gets or sets the document kind (web page, PDF, or DOCX) of the result.</summary>
    public DocKind DocKind { get; set; }
}

/// <summary>Represents the response containing ranked search results.</summary>
public class SearchResponse
{
    /// <summary>Gets or sets the list of ranked search result items.</summary>
    public List<SearchResultItem> Items { get; set; } = new();
    /// <summary>Gets or sets the total number of match items found.</summary>
    public int TotalMatches { get; set; }
}
