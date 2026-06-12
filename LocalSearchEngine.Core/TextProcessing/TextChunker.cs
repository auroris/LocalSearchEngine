namespace LocalSearchEngine.Core.TextProcessing;

/// <summary>
/// Splits extracted document text into overlapping word windows for embedding.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Splits the given text into overlapping chunks of a specified maximum word count.
    /// </summary>
    /// <param name="text">The raw text string to split.</param>
    /// <param name="chunkSize">The maximum number of words per chunk.</param>
    /// <param name="overlap">The number of words to overlap between consecutive chunks.</param>
    /// <returns>An enumerable of chunk strings.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="chunkSize"/> is less than or equal to 0, or <paramref name="overlap"/> is negative or not less than <paramref name="chunkSize"/>.</exception>
    public static IEnumerable<string> Chunk(string text, int chunkSize = 150, int overlap = 30)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (overlap < 0 || overlap >= chunkSize) throw new ArgumentOutOfRangeException(nameof(overlap), "overlap must be in [0, chunkSize)");

        if (string.IsNullOrWhiteSpace(text)) yield break;

        // Tokenize into (start, length) ranges over the original string rather than a string[].
        // For a large document that avoids allocating one substring per word; only the emitted
        // chunks (far fewer) are ever materialized into strings.
        var words = SplitWordRanges(text);
        if (words.Count == 0) yield break;

        int step = chunkSize - overlap;
        for (int i = 0; i < words.Count; i += step)
        {
            int take = Math.Min(chunkSize, words.Count - i);
            yield return JoinRange(text, words, i, take);

            // Stop once this window already reaches the end; otherwise the next
            // iteration would re-emit only the overlap region.
            if (i + chunkSize >= words.Count) yield break;
        }
    }

    /// <summary>
    /// Scans the text for whitespace-delimited word spans and records their indices.
    /// </summary>
    /// <param name="text">The raw text string to parse.</param>
    /// <returns>A list of tuples representing the start index and length of each word span.</returns>
    private static List<(int Start, int Length)> SplitWordRanges(string text)
    {
        var ranges = new List<(int, int)>();
        int i = 0;
        int n = text.Length;
        while (i < n)
        {
            while (i < n && IsSeparator(text[i])) i++;
            if (i >= n) break;
            int start = i;
            while (i < n && !IsSeparator(text[i])) i++;
            ranges.Add((start, i - start));
        }
        return ranges;
    }

    /// <summary>
    /// Checks if a character is a whitespace separator.
    /// </summary>
    /// <param name="c">The character to check.</param>
    /// <returns><c>true</c> if the character is a space, newline, carriage return, or tab; otherwise, <c>false</c>.</returns>
    private static bool IsSeparator(char c) => c is ' ' or '\n' or '\r' or '\t';

    /// <summary>
    /// Joins a range of word spans into a single space-separated string chunk in one allocation.
    /// </summary>
    /// <param name="text">The source text string containing the words.</param>
    /// <param name="words">The list of word span ranges.</param>
    /// <param name="offset">The starting index in the word list.</param>
    /// <param name="count">The number of words to join.</param>
    /// <returns>A space-separated string chunk.</returns>
    private static string JoinRange(string text, List<(int Start, int Length)> words, int offset, int count)
    {
        int total = count - 1; // one separator between each adjacent pair of words
        for (int k = 0; k < count; k++) total += words[offset + k].Length;

        return string.Create(total, (text, words, offset, count), static (span, state) =>
        {
            var (source, ranges, off, cnt) = state;
            int pos = 0;
            for (int k = 0; k < cnt; k++)
            {
                if (k > 0) span[pos++] = ' ';
                var (start, length) = ranges[off + k];
                source.AsSpan(start, length).CopyTo(span[pos..]);
                pos += length;
            }
        });
    }
}
