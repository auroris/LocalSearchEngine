namespace LocalSearchEngine.Core;

/// <summary>A semantic (vector) hit: a chunk and its cosine distance to the query (lower = nearer).</summary>
public readonly record struct VectorCandidate(string Url, string Text, bool IsHeading, double Distance);

/// <summary>An exact keyword (FTS5) hit for the query phrase.</summary>
public readonly record struct KeywordCandidate(string Url, string Text, bool IsHeading);

/// <summary>
/// Pure ranking logic, free of any database or embedding dependency so it can be
/// unit-tested directly. Returns <em>every</em> result that is "close enough" — vector
/// hits at or above the similarity threshold plus all exact keyword matches — ordered
/// by an explicit, interpretable relevance score. There is no result-count cap.
/// </summary>
public static class SearchRanker
{
    public static List<SearchResultItem> Rank(
        IEnumerable<VectorCandidate> vectorHits,
        IEnumerable<KeywordCandidate> keywordHits,
        string query,
        SearchSettings settings)
    {
        var byUrl = new Dictionary<string, Aggregate>(StringComparer.OrdinalIgnoreCase);

        // Semantic pass: keep only chunks that clear the "close enough" threshold.
        foreach (var hit in vectorHits)
        {
            double similarity = 1.0 - hit.Distance; // confirmed cosine distance: lower distance = more similar
            if (similarity < settings.MinSimilarity) continue;

            var agg = GetOrCreate(byUrl, hit.Url);
            if (similarity > agg.Similarity || agg.Text.Length == 0)
            {
                agg.Similarity = Math.Max(agg.Similarity, similarity);
                agg.Text = hit.Text; // representative snippet = most similar chunk
            }
            agg.MatchedHeading |= hit.IsHeading;
        }

        // Keyword pass: exact phrase matches are always relevant, threshold or not.
        foreach (var hit in keywordHits)
        {
            var agg = GetOrCreate(byUrl, hit.Url);
            agg.ExactPhrase = true;
            agg.MatchedHeading |= hit.IsHeading;
            if (agg.Text.Length == 0) agg.Text = hit.Text; // only if no vector snippet chosen
        }

        var results = new List<SearchResultItem>(byUrl.Count);
        foreach (var agg in byUrl.Values)
        {
            double score = agg.Similarity;
            if (agg.ExactPhrase) score += settings.ExactPhraseBoost;
            if (agg.MatchedHeading) score += settings.HeadingBoost;
            if (UrlFileNameContains(agg.Url, query)) score += settings.FilenameBoost;
            if (agg.Text.Contains(query, StringComparison.OrdinalIgnoreCase)) score += settings.TermInTextBoost;

            results.Add(new SearchResultItem
            {
                Url = agg.Url,
                Text = agg.Text,
                Similarity = agg.Similarity,
                Score = score
            });
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results;
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

    private static bool UrlFileNameContains(string url, string query)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var segment = uri.Segments.LastOrDefault()?.TrimEnd('/');
        if (segment is null) return false;
        return Path.GetFileNameWithoutExtension(segment).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Aggregate
    {
        public string Url = string.Empty;
        public string Text = string.Empty;
        public double Similarity;
        public bool ExactPhrase;
        public bool MatchedHeading;
    }
}

public class SearchResultItem
{
    public string Url { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    /// <summary>Cosine similarity (1 - distance) of the best matching chunk; 0 for keyword-only hits.</summary>
    public double Similarity { get; set; }
    /// <summary>Final relevance score: similarity plus exact-match/heading/filename boosts.</summary>
    public double Score { get; set; }
}

public class SearchResponse
{
    public List<SearchResultItem> Items { get; set; } = new();
    public int TotalMatches { get; set; }
}
