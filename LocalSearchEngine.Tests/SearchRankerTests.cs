using LocalSearchEngine.Core;
using Xunit;

namespace LocalSearchEngine.Tests;

public class SearchRankerTests
{
    private static SearchSettings Settings() => new()
    {
        MinSimilarity = 0.5,
        ExactPhraseBoost = 0.5,
        HeadingBoost = 0.3,
        FilenameBoost = 0.4,
        TermInTextBoost = 0.2
    };

    private static readonly KeywordCandidate[] NoKeywords = Array.Empty<KeywordCandidate>();
    private static readonly VectorCandidate[] NoVectors = Array.Empty<VectorCandidate>();

    [Fact]
    public void Excludes_results_below_similarity_threshold()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/a", "alpha", false, 0.10), // similarity 0.90 -> in
            new VectorCandidate("https://x/b", "beta",  false, 0.40), // similarity 0.60 -> in
            new VectorCandidate("https://x/c", "gamma", false, 0.80), // similarity 0.20 -> out
        };

        var results = SearchRanker.Rank(vectors, NoKeywords, "zzz", Settings());

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.Url == "https://x/c");
    }

    [Fact]
    public void Orders_by_descending_score()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/b", "beta",  false, 0.40), // 0.60
            new VectorCandidate("https://x/a", "alpha", false, 0.10), // 0.90
        };

        var results = SearchRanker.Rank(vectors, NoKeywords, "zzz", Settings());

        Assert.Equal("https://x/a", results[0].Url);
        Assert.Equal("https://x/b", results[1].Url);
    }

    [Fact]
    public void Returns_all_qualifying_results_with_no_count_cap()
    {
        var vectors = Enumerable.Range(0, 250)
            .Select(i => new VectorCandidate($"https://x/{i}", "t", false, 0.05))
            .ToArray();

        var results = SearchRanker.Rank(vectors, NoKeywords, "zzz", Settings());

        Assert.Equal(250, results.Count);
    }

    [Fact]
    public void Exact_keyword_match_is_always_included_even_with_no_vector_hit()
    {
        var keywords = new[] { new KeywordCandidate("https://x/k", "irrelevant body", false) };

        var results = SearchRanker.Rank(NoVectors, keywords, "nomatchhere", Settings());

        Assert.Single(results);
        // similarity 0 + exact-phrase boost only (query absent from text and filename)
        Assert.Equal(0.5, results[0].Score, 6);
    }

    [Fact]
    public void Filename_match_boosts_score()
    {
        var vectors = new[] { new VectorCandidate("https://x/installation-guide", "body", false, 0.50) }; // 0.50

        var results = SearchRanker.Rank(vectors, NoKeywords, "guide", Settings());

        // similarity 0.50 + filename boost 0.40
        Assert.Equal(0.90, results[0].Score, 6);
    }

    [Fact]
    public void Heading_match_boosts_score()
    {
        var vectors = new[] { new VectorCandidate("https://x/a", "body", true, 0.40) }; // 0.60, heading

        var results = SearchRanker.Rank(vectors, NoKeywords, "zzz", Settings());

        Assert.Equal(0.90, results[0].Score, 6); // 0.60 + heading 0.30
    }

    [Fact]
    public void Query_appearing_in_text_boosts_score()
    {
        var vectors = new[] { new VectorCandidate("https://x/a", "the quick brown fox", false, 0.40) }; // 0.60

        var results = SearchRanker.Rank(vectors, NoKeywords, "quick", Settings());

        Assert.Equal(0.80, results[0].Score, 6); // 0.60 + term-in-text 0.20
    }

    [Fact]
    public void Deduplicates_by_url_keeping_most_similar_chunk_as_snippet()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/a", "low chunk",  false, 0.50), // 0.50
            new VectorCandidate("https://x/a", "high chunk", false, 0.20), // 0.80
        };

        var results = SearchRanker.Rank(vectors, NoKeywords, "zzz", Settings());

        Assert.Single(results);
        Assert.Equal(0.80, results[0].Similarity, 6);
        Assert.Equal("high chunk", results[0].Text);
    }

    [Fact]
    public void Combines_vector_and_keyword_signals_for_the_same_url()
    {
        var vectors = new[] { new VectorCandidate("https://x/guide", "intro guide content", false, 0.30) }; // 0.70
        var keywords = new[] { new KeywordCandidate("https://x/guide", "intro guide content", true) };       // exact + heading

        var results = SearchRanker.Rank(vectors, keywords, "guide", Settings());

        Assert.Single(results);
        // 0.70 sim + 0.50 exact + 0.30 heading + 0.40 filename + 0.20 term-in-text
        Assert.Equal(2.10, results[0].Score, 6);
    }
}
