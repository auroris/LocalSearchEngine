using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Searching;
using Xunit;

namespace LocalSearchEngine.Tests;

public class SearchRankerTests
{
    private static SearchSettings Settings() => new()
    {
        MaxDistance = 0.5,
        ReciprocalRankConstant = 60,
        SemanticWeight = 1.0,
        KeywordWeight = 1.0,
        ExactPhraseBoost = 0.35,
        ProximityBoost = 0.15,
        HeadingBoost = 0.3,
        TitleBoost = 0.35,
        FilenameBoost = 0.4,
        TermInTextBoost = 0.2,
        MultiChunkBoost = 0.1,
        NonHtmlPenalty = 0.15
    };

    private static readonly KeywordCandidate[] NoKeywords = [];
    private static readonly VectorCandidate[] NoVectors = [];

    [Fact]
    public void Excludes_distant_vector_results_but_keeps_lexical_candidates()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/near", "alpha", false, 0.10),
            new VectorCandidate("https://x/rescued", "useful lexical text", false, 0.80)
        };
        var keywords = new[]
        {
            new KeywordCandidate("https://x/rescued", "useful lexical text", false, Bm25: -2)
        };

        var results = SearchRanker.Rank(vectors, keywords, "useful", Settings());

        Assert.Equal(2, results.Count);
        Assert.Equal(0, results.Single(result => result.Url.EndsWith("rescued")).Similarity);
    }

    [Fact]
    public void Semantic_rank_is_derived_from_distance_not_input_order()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/weaker", "body", false, 0.40),
            new VectorCandidate("https://x/stronger", "body", false, 0.10)
        };

        var results = SearchRanker.Rank(vectors, NoKeywords, "unseen", Settings());

        Assert.Equal("https://x/stronger", results[0].Url);
    }

    [Fact]
    public void Returns_all_qualifying_results_with_no_result_cap()
    {
        var vectors = Enumerable.Range(0, 250)
            .Select(i => new VectorCandidate($"https://x/{i}", "body", false, 0.05))
            .ToArray();

        var results = SearchRanker.Rank(vectors, NoKeywords, "unseen", Settings());

        Assert.Equal(250, results.Count);
    }

    [Fact]
    public void Structural_fields_contribute_bounded_coverage_boosts()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/user-guide.html", "body", true, 0.20)
        };
        var titles = new Dictionary<string, string?>
        {
            ["https://x/user-guide.html"] = "Complete User Guide"
        };

        var result = Assert.Single(SearchRanker.Rank(vectors, NoKeywords, "user guide", Settings(), titles));

        // semantic RRF 1 + heading .3 + title .35 + filename .4
        Assert.Equal(2.05, result.Score, 6);
        Assert.Equal("Complete User Guide", result.Title);
    }

    [Fact]
    public void Text_term_coverage_is_proportional()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/one", "alpha only", false, 0.10),
            new VectorCandidate("https://x/two", "alpha and beta", false, 0.20)
        };

        var results = SearchRanker.Rank(vectors, NoKeywords, "alpha beta gamma", Settings());

        Assert.Equal("https://x/two", results[0].Url);
    }

    [Fact]
    public void Deduplicates_by_url_and_uses_the_closest_semantic_chunk()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/a", "weaker chunk", false, 0.50),
            new VectorCandidate("https://x/a", "closest chunk", false, 0.20)
        };

        var result = Assert.Single(SearchRanker.Rank(vectors, NoKeywords, "unseen", Settings()));

        Assert.Equal(0.80, result.Similarity, 6);
        Assert.Equal("closest chunk", result.Text);
    }

    [Fact]
    public void Bm25_value_determines_lexical_rank_not_input_order()
    {
        var keywords = new[]
        {
            new KeywordCandidate("https://x/weaker", "body", false, Bm25: -1),
            new KeywordCandidate("https://x/stronger", "body", false, Bm25: -10)
        };

        var results = SearchRanker.Rank(NoVectors, keywords, "unseen", Settings());

        Assert.Equal("https://x/stronger", results[0].Url);
    }

    [Fact]
    public void Keyword_candidate_is_included_without_a_vector_match()
    {
        var keywords = new[]
        {
            new KeywordCandidate("https://x/lexical", "rare deployment incantation", false, Bm25: -3)
        };

        var result = Assert.Single(SearchRanker.Rank(NoVectors, keywords, "deployment", Settings()));

        Assert.Equal("https://x/lexical", result.Url);
        Assert.True(result.Score > 1.0);
        Assert.Equal(0, result.Similarity);
    }

    [Fact]
    public void Reciprocal_rank_fusion_rewards_agreement_between_retrievers()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/hybrid", "body", false, 0.20),
            new VectorCandidate("https://x/semantic", "body", false, 0.10)
        };
        var keywords = new[]
        {
            new KeywordCandidate("https://x/hybrid", "body", false, Bm25: -1)
        };

        var results = SearchRanker.Rank(vectors, keywords, "unseen", Settings());

        Assert.Equal("https://x/hybrid", results[0].Url);
    }

    [Fact]
    public void Exact_phrase_is_a_bonus_not_a_hard_tier()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/hybrid", "alpha separated from beta", false, 0.10)
        };
        var keywords = new[]
        {
            new KeywordCandidate("https://x/phrase", "alpha beta", false, ExactPhrase: true, Bm25: -2),
            new KeywordCandidate("https://x/hybrid", "alpha separated from beta", false, Bm25: -1)
        };

        var results = SearchRanker.Rank(vectors, keywords, "alpha beta", Settings());

        Assert.Equal("https://x/hybrid", results[0].Url);
        Assert.Contains(results, result => result.Url == "https://x/phrase");
    }

    [Fact]
    public void Exact_phrase_beats_an_otherwise_comparable_loose_match()
    {
        var keywords = new[]
        {
            new KeywordCandidate("https://x/loose", "alpha several words before beta", false, Bm25: -2),
            new KeywordCandidate("https://x/phrase", "alpha beta", false, ExactPhrase: true, Bm25: -2)
        };

        var results = SearchRanker.Rank(NoVectors, keywords, "alpha beta", Settings());

        Assert.Equal("https://x/phrase", results[0].Url);
    }

    [Fact]
    public void Close_terms_rank_above_widely_separated_terms()
    {
        var keywords = new[]
        {
            new KeywordCandidate("https://x/far", "alpha one two three four beta", false, Bm25: -2),
            new KeywordCandidate("https://x/close", "beta alpha", false, Bm25: -2)
        };

        var results = SearchRanker.Rank(NoVectors, keywords, "alpha beta", Settings());

        Assert.Equal("https://x/close", results[0].Url);
    }

    [Fact]
    public void Better_term_coverage_can_overcome_a_one_place_bm25_difference()
    {
        var keywords = new[]
        {
            new KeywordCandidate("https://x/partial", "alpha", false, Bm25: -3),
            new KeywordCandidate("https://x/better", "alpha and beta", false, Bm25: -2)
        };

        var results = SearchRanker.Rank(NoVectors, keywords, "alpha beta gamma", Settings());

        Assert.Equal("https://x/better", results[0].Url);
    }

    [Fact]
    public void Multiple_matching_chunks_add_diminishing_evidence()
    {
        var oneChunk = new[]
        {
            new KeywordCandidate("https://x/a", "alpha", false, Bm25: -2)
        };
        var twoChunks = new[]
        {
            new KeywordCandidate("https://x/a", "alpha first", false, Bm25: -2),
            new KeywordCandidate("https://x/a", "alpha second", false, Bm25: -1)
        };

        var one = Assert.Single(SearchRanker.Rank(NoVectors, oneChunk, "alpha", Settings()));
        var two = Assert.Single(SearchRanker.Rank(NoVectors, twoChunks, "alpha", Settings()));

        Assert.Equal(Settings().MultiChunkBoost / 2, two.Score - one.Score, 6);
    }

    [Fact]
    public void Lexical_snippet_is_used_when_it_explains_the_query_better()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/a", "semantically related introduction", false, 0.10)
        };
        var keywords = new[]
        {
            new KeywordCandidate("https://x/a", "exact deployment procedure", false, Bm25: -2)
        };

        var result = Assert.Single(SearchRanker.Rank(vectors, keywords, "deployment procedure", Settings()));

        Assert.Equal("exact deployment procedure", result.Text);
    }

    [Fact]
    public void Non_html_penalty_is_soft_not_an_absolute_tier()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/page.html", "body", false, 0.10),
            new VectorCandidate("https://x/authoritative.pdf", "body", false, 0.20)
        };
        var keywords = new[]
        {
            new KeywordCandidate("https://x/authoritative.pdf", "body", false, Bm25: -2)
        };
        var docKinds = new Dictionary<string, DocKind>
        {
            ["https://x/page.html"] = DocKind.Html,
            ["https://x/authoritative.pdf"] = DocKind.Pdf
        };

        var results = SearchRanker.Rank(vectors, keywords, "unseen", Settings(), docKinds: docKinds);

        Assert.Equal("https://x/authoritative.pdf", results[0].Url);
        Assert.Equal(DocKind.Pdf, results[0].DocKind);
    }

    [Fact]
    public void Equal_evidence_prefers_html_by_the_configured_penalty()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/doc.pdf", "body", false, 0.10),
            new VectorCandidate("https://x/page.html", "body", false, 0.10)
        };
        var docKinds = new Dictionary<string, DocKind>
        {
            ["https://x/doc.pdf"] = DocKind.Pdf,
            ["https://x/page.html"] = DocKind.Html
        };

        var results = SearchRanker.Rank(vectors, NoKeywords, "unseen", Settings(), docKinds: docKinds);

        Assert.Equal("https://x/page.html", results[0].Url);
    }

    [Fact]
    public void Missing_document_kind_is_treated_as_html()
    {
        var result = Assert.Single(SearchRanker.Rank(
            [new VectorCandidate("https://x/unknown", "body", false, 0.10)],
            NoKeywords,
            "unseen",
            Settings(),
            docKinds: new Dictionary<string, DocKind>()));

        Assert.Equal(DocKind.Html, result.DocKind);
    }
}
