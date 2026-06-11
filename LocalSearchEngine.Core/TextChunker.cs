namespace LocalSearchEngine.Core;

/// <summary>
/// Splits extracted document text into overlapping word windows for embedding.
/// </summary>
public static class TextChunker
{
    private static readonly char[] WordSeparators = { ' ', '\n', '\r', '\t' };

    /// <summary>
    /// Yields chunks of up to <paramref name="chunkSize"/> words, advancing by
    /// (chunkSize - overlap) words each step. The trailing window is emitted once
    /// and not repeated as a redundant overlap-only fragment.
    /// </summary>
    public static IEnumerable<string> Chunk(string text, int chunkSize = 150, int overlap = 30)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (overlap < 0 || overlap >= chunkSize) throw new ArgumentOutOfRangeException(nameof(overlap), "overlap must be in [0, chunkSize)");

        if (string.IsNullOrWhiteSpace(text)) yield break;

        var words = text.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) yield break;

        int step = chunkSize - overlap;
        for (int i = 0; i < words.Length; i += step)
        {
            int take = Math.Min(chunkSize, words.Length - i);
            yield return string.Join(" ", words, i, take);

            // Stop once this window already reaches the end; otherwise the next
            // iteration would re-emit only the overlap region.
            if (i + chunkSize >= words.Length) yield break;
        }
    }
}
