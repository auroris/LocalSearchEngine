using System;
using System.Linq;
using LocalSearchEngine.Core.TextProcessing;
using Xunit;

namespace LocalSearchEngine.Tests;

public class TextChunkerTests
{
    [Fact]
    public void Chunk_returns_nothing_for_empty_text()
    {
        Assert.Empty(TextChunker.Chunk(""));
        Assert.Empty(TextChunker.Chunk("   \n\t  "));
    }

    [Fact]
    public void Chunk_returns_single_chunk_when_text_fits()
    {
        var text = "This is a short sentence that easily fits in a single chunk.";
        var chunks = TextChunker.Chunk(text, chunkSize: 150, overlap: 30).ToList();
        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }

    [Fact]
    public void Chunk_splits_long_text_into_multiple_chunks()
    {
        // Generate a long text with multiple sentences to trigger splitting
        var sentences = Enumerable.Range(0, 50)
            .Select(i => $"This is sentence number {i} in the long document text.")
            .ToList();
        var text = string.Join(" ", sentences);
        
        // Use a small chunk size to force multiple chunks
        var chunks = TextChunker.Chunk(text, chunkSize: 30, overlap: 10).ToList();
        
        Assert.True(chunks.Count > 1);
        // Ensure no chunk is empty
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c)));
    }

    [Fact]
    public void Chunk_respects_sentence_boundaries()
    {
        var text = "First sentence is here. Second sentence starts here.";
        // Set chunk size small enough that it splits at a sentence boundary
        var chunks = TextChunker.Chunk(text, chunkSize: 10, overlap: 2).ToList();
        
        Assert.True(chunks.Count >= 2);
        // The first chunk should end with a period or complete sentence.
        Assert.Contains("First sentence is here.", chunks[0]);
    }

    [Fact]
    public void Chunk_rejects_overlap_not_smaller_than_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextChunker.Chunk("a b c", chunkSize: 10, overlap: 10).ToList());
    }
}
