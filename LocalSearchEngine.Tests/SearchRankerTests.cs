using LocalSearchEngine.Core.Crawling.Policies;
using LocalSearchEngine.Core.Searching;
using Xunit;

namespace LocalSearchEngine.Tests;

public class SearchRankerTests
{
    private static SearchSettings Settings() => new()
    {
        MaxDistance = 0.5,
        HeadingBoost = 0.3,
        TitleBoost = 0.35,
        FilenameBoost = 0.4,
        TermInTextBoost = 0.2
    };

    private static readonly KeywordCandidate[] NoKeywords = Array.Empty<KeywordCandidate>();
    private static readonly VectorCandidate[] NoVectors = Array.Empty<VectorCandidate>();

    // --- Threshold, dedup, and within-group score (one doc tier, one match tier) ---

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
    public void Filename_match_boosts_score()
    {
        var vectors = new[] { new VectorCandidate("https://x/installation-guide", "body", false, 0.50) }; // 0.50

        var results = SearchRanker.Rank(vectors, NoKeywords, "guide", Settings());

        // similarity 0.50 + filename boost 0.40
        Assert.Equal(0.90, results[0].Score, 6);
    }

    [Fact]
    public void Filename_match_boosts_multi_word_query_across_slug_separators()
    {
        // Query words are separated by spaces; the slug uses '-'. They should still match.
        var vectors = new[] { new VectorCandidate("https://x/user-guide.html", "body", false, 0.50) }; // 0.50

        var results = SearchRanker.Rank(vectors, NoKeywords, "user guide", Settings());

        Assert.Equal(0.90, results[0].Score, 6); // 0.50 + filename 0.40
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
    public void Title_containing_query_boosts_and_is_returned()
    {
        var vectors = new[] { new VectorCandidate("https://x/a", "body", false, 0.40) }; // 0.60
        var titles = new Dictionary<string, string?> { ["https://x/a"] = "Installation Guide" };

        var results = SearchRanker.Rank(vectors, NoKeywords, "guide", Settings(), titles);

        Assert.Equal("Installation Guide", results[0].Title);
        Assert.Equal(0.95, results[0].Score, 6); // 0.60 + title 0.35 (query absent from body/filename)
    }

    // --- Keyword hits: exact/all-terms is now a tier, not an additive Score component ---

    [Fact]
    public void Keyword_match_is_always_included_even_with_no_vector_hit()
    {
        var keywords = new[] { new KeywordCandidate("https://x/k", "irrelevant body", false) };

        var results = SearchRanker.Rank(NoVectors, keywords, "nomatchhere", Settings());

        Assert.Single(results);
        // similarity 0; the exact-phrase signal is a tier, not a boost, and the query is absent from
        // the text/filename/title, so the fine score is 0.
        Assert.Equal(0.0, results[0].Score, 6);
    }

    [Fact]
    public void Combines_vector_and_keyword_signals_for_the_same_url()
    {
        var vectors = new[] { new VectorCandidate("https://x/guide", "intro guide content", false, 0.30) }; // 0.70
        var keywords = new[] { new KeywordCandidate("https://x/guide", "intro guide content", true) };       // exact + heading

        var results = SearchRanker.Rank(vectors, keywords, "guide", Settings());

        Assert.Single(results);
        // 0.70 sim + 0.30 heading + 0.40 filename + 0.20 term-in-text (no exact-phrase additive boost)
        Assert.Equal(1.60, results[0].Score, 6);
    }

    [Fact]
    public void Exact_and_all_terms_hits_on_one_url_collapse_to_a_single_result()
    {
        var keywords = new[]
        {
            new KeywordCandidate("https://x/a", "body", false, ExactPhrase: false),
            new KeywordCandidate("https://x/a", "body", false, ExactPhrase: true),
        };

        var results = SearchRanker.Rank(NoVectors, keywords, "zzz", Settings());

        Assert.Single(results);
        Assert.Equal(0.0, results[0].Score, 6); // no vector similarity, no fine-score boosts
    }

    // --- Match tier: exact phrase ranks above all-terms / semantic-only within a doc group ---

    [Fact]
    public void Exact_phrase_ranks_above_all_terms_even_with_a_lower_fine_score()
    {
        // Same doc type. The all-terms URL has the stronger similarity, yet the exact-phrase URL
        // still comes first — the exact tier is hard, not a score nudge.
        var vectors = new[]
        {
            new VectorCandidate("https://x/exact",    "body", false, 0.40), // sim 0.60
            new VectorCandidate("https://x/allterms", "body", false, 0.10), // sim 0.90
        };
        var keywords = new[]
        {
            new KeywordCandidate("https://x/exact",    "body", false, ExactPhrase: true),
            new KeywordCandidate("https://x/allterms", "body", false, ExactPhrase: false),
        };

        var results = SearchRanker.Rank(vectors, keywords, "zzz", Settings());

        Assert.Equal("https://x/exact",    results[0].Url);
        Assert.Equal("https://x/allterms", results[1].Url);
        Assert.True(results[1].Score > results[0].Score); // proves ordering wasn't by score
    }

    [Fact]
    public void Vector_only_hit_shares_the_tier_with_all_terms_ordered_by_fine_score()
    {
        // A semantic-only hit (no keyword match) and an all-terms hit sit in the same match tier:
        // the higher fine score wins regardless of which signal produced it.
        var vectors = new[]
        {
            new VectorCandidate("https://x/vectoronly", "body", false, 0.10), // sim 0.90, no keyword
            new VectorCandidate("https://x/allterms",   "body", false, 0.40), // sim 0.60
        };
        var keywords = new[] { new KeywordCandidate("https://x/allterms", "body", false, ExactPhrase: false) };

        var results = SearchRanker.Rank(vectors, keywords, "zzz", Settings());

        Assert.Equal("https://x/vectoronly", results[0].Url); // higher fine score, same tier
        Assert.Equal("https://x/allterms",   results[1].Url);
    }

    // --- Doc tier: web pages rank above PDFs/DOCX, dominating the match tier and fine score ---

    [Fact]
    public void Web_page_ranks_above_a_pdf_with_a_higher_fine_score()
    {
        var vectors = new[]
        {
            new VectorCandidate("https://x/page.html", "body", false, 0.40), // sim 0.60
            new VectorCandidate("https://x/doc.pdf",   "body", false, 0.05), // sim 0.95
        };
        var docKinds = new Dictionary<string, DocKind>
        {
            ["https://x/page.html"] = DocKind.Html,
            ["https://x/doc.pdf"]   = DocKind.Pdf,
        };

        var results = SearchRanker.Rank(vectors, NoKeywords, "zzz", Settings(), titles: null, docKinds: docKinds);

        Assert.Equal("https://x/page.html", results[0].Url); // web page first despite lower score
        Assert.Equal("https://x/doc.pdf",   results[1].Url);
        Assert.True(results[1].Score > results[0].Score);
    }

    [Fact]
    public void Web_page_with_all_terms_ranks_above_a_pdf_with_an_exact_phrase()
    {
        // Doc type is the OUTER tier, so a web page that only matches all-terms still beats a PDF
        // that matches the exact phrase.
        var vectors = new[]
        {
            new VectorCandidate("https://x/page.html", "body", false, 0.40), // sim 0.60
            new VectorCandidate("https://x/doc.pdf",   "body", false, 0.10), // sim 0.90
        };
        var keywords = new[]
        {
            new KeywordCandidate("https://x/page.html", "body", false, ExactPhrase: false), // all-terms
            new KeywordCandidate("https://x/doc.pdf",   "body", false, ExactPhrase: true),  // exact phrase
        };
        var docKinds = new Dictionary<string, DocKind>
        {
            ["https://x/page.html"] = DocKind.Html,
            ["https://x/doc.pdf"]   = DocKind.Pdf,
        };

        var results = SearchRanker.Rank(vectors, keywords, "zzz", Settings(), titles: null, docKinds: docKinds);

        Assert.Equal("https://x/page.html", results[0].Url);
        Assert.Equal("https://x/doc.pdf",   results[1].Url);
    }

    [Fact]
    public void Docx_is_grouped_with_pdf_below_web_pages()
    {
        // "Web page vs anything else" — DOCX is demoted just like PDF.
        var vectors = new[]
        {
            new VectorCandidate("https://x/page.html", "body", false, 0.45), // sim 0.55
            new VectorCandidate("https://x/doc.docx",  "body", false, 0.05), // sim 0.95
        };
        var docKinds = new Dictionary<string, DocKind>
        {
            ["https://x/page.html"] = DocKind.Html,
            ["https://x/doc.docx"]  = DocKind.Docx,
        };

        var results = SearchRanker.Rank(vectors, NoKeywords, "zzz", Settings(), titles: null, docKinds: docKinds);

        Assert.Equal("https://x/page.html", results[0].Url);
        Assert.Equal(DocKind.Docx, results[1].DocKind);
    }

    [Fact]
    public void Missing_doc_kind_is_treated_as_a_web_page()
    {
        // A URL with no DocKind entry must not be demoted below real web pages.
        var vectors = new[]
        {
            new VectorCandidate("https://x/unknown", "body", false, 0.40), // sim 0.60, no docKinds entry
            new VectorCandidate("https://x/pdf",     "body", false, 0.05), // sim 0.95, PDF
        };
        var docKinds = new Dictionary<string, DocKind> { ["https://x/pdf"] = DocKind.Pdf };

        var results = SearchRanker.Rank(vectors, NoKeywords, "zzz", Settings(), titles: null, docKinds: docKinds);

        Assert.Equal("https://x/unknown", results[0].Url); // treated as a web page, beats the PDF
    }

    [Fact]
    public void Result_exposes_doc_kind()
    {
        var vectors = new[] { new VectorCandidate("https://x/doc.pdf", "body", false, 0.10) };
        var docKinds = new Dictionary<string, DocKind> { ["https://x/doc.pdf"] = DocKind.Pdf };

        var results = SearchRanker.Rank(vectors, NoKeywords, "zzz", Settings(), titles: null, docKinds: docKinds);

        Assert.Equal(DocKind.Pdf, results[0].DocKind);
    }
}
