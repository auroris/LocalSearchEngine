using LocalSearchEngine.Core;
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
        var text = string.Join(" ", Enumerable.Range(0, 50).Select(i => $"w{i}"));
        var chunks = TextChunker.Chunk(text, chunkSize: 150, overlap: 30).ToList();
        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }

    [Fact]
    public void Chunk_overlaps_consecutive_windows()
    {
        var words = Enumerable.Range(0, 200).Select(i => $"w{i}").ToArray();
        var chunks = TextChunker.Chunk(string.Join(" ", words), chunkSize: 150, overlap: 30).ToList();

        Assert.Equal(2, chunks.Count);
        Assert.Equal(words.Take(150), chunks[0].Split(' '));
        // Second window starts at 150 - 30 = 120.
        Assert.Equal(words.Skip(120), chunks[1].Split(' '));
    }

    [Fact]
    public void Chunk_does_not_emit_redundant_overlap_only_tail()
    {
        // Exactly one chunk-size of words: the tail must not produce a second,
        // fully-contained overlap fragment.
        var words = Enumerable.Range(0, 150).Select(i => $"w{i}").ToArray();
        var chunks = TextChunker.Chunk(string.Join(" ", words), chunkSize: 150, overlap: 30).ToList();
        Assert.Single(chunks);
    }

    [Fact]
    public void Chunk_rejects_overlap_not_smaller_than_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextChunker.Chunk("a b c", chunkSize: 10, overlap: 10).ToList());
    }
}
