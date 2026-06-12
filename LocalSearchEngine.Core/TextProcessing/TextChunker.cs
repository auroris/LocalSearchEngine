// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LocalSearchEngine.Core.TextProcessing;

/// <summary>
/// Splits document text into overlapping chunks, attempting to leave semantic meaning intact.
/// Supports plain text and markdown formatting splitting options.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Splits the given text into overlapping chunks of a specified maximum word/token count.
    /// </summary>
    /// <param name="text">The raw text string to split.</param>
    /// <param name="chunkSize">The maximum approximate word count per chunk.</param>
    /// <param name="overlap">The approximate word count to overlap between consecutive chunks.</param>
    /// <returns>An enumerable of chunk strings.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="chunkSize"/> is less than or equal to 0, or <paramref name="overlap"/> is negative or not less than <paramref name="chunkSize"/>.</exception>
    public static IEnumerable<string> Chunk(string text, int chunkSize = 150, int overlap = 30)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (overlap < 0 || overlap >= chunkSize) throw new ArgumentOutOfRangeException(nameof(overlap), "overlap must be in [0, chunkSize)");

        if (string.IsNullOrWhiteSpace(text)) return Enumerable.Empty<string>();

        // Approximate token conversion: ~1.33 tokens per word
        int maxTokens = (int)(chunkSize * 1.33);
        int overlapTokens = (int)(overlap * 1.33);

        var lines = SplitPlainTextLines(text, maxTokensPerLine: 100);
        return SplitPlainTextParagraphs(lines, maxTokens, overlapTokens);
    }

    /// <summary>
    /// Represents a list of strings along with their token counts, optimized to minimize tokenizer calls.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="StringListWithTokenCount"/> class.
    /// </remarks>
    /// <param name="tokenCounter">The token counter delegate, or null to use character length heuristics.</param>
    private sealed class StringListWithTokenCount(TextChunker.TokenCounter? tokenCounter)
    {
        private readonly TokenCounter? _tokenCounter = tokenCounter;

        /// <summary>
        /// Adds a string value and calculates its token count.
        /// </summary>
        /// <param name="value">The string value to add.</param>
        public void Add(string value) => this.Values.Add((value, this._tokenCounter is null ? GetDefaultTokenCount(value.Length) : this._tokenCounter(value)));

        /// <summary>
        /// Adds a string value with a pre-calculated token count.
        /// </summary>
        /// <param name="value">The string value to add.</param>
        /// <param name="tokenCount">The number of tokens in the string.</param>
        public void Add(string value, int tokenCount) => this.Values.Add((value, tokenCount));

        /// <summary>
        /// Adds all values from another string list with token counts.
        /// </summary>
        /// <param name="range">The list of values to add.</param>
        public void AddRange(StringListWithTokenCount range) => this.Values.AddRange(range.Values);

        /// <summary>
        /// Removes a range of elements from the list.
        /// </summary>
        /// <param name="index">The zero-based starting index of the range to remove.</param>
        /// <param name="count">The number of elements to remove.</param>
        public void RemoveRange(int index, int count) => this.Values.RemoveRange(index, count);

        /// <summary>
        /// Gets the number of elements contained in the list.
        /// </summary>
        public int Count => this.Values.Count;

        /// <summary>
        /// Converts the list elements to a standard list of strings.
        /// </summary>
        /// <returns>A list of strings.</returns>
        public List<string> ToStringList() => this.Values.Select(v => v.Value).ToList();

        /// <summary>
        /// Gets the internal list containing the string values and token counts.
        /// </summary>
        private List<(string Value, int TokenCount)> Values { get; } = [];

        /// <summary>
        /// Gets the string value at the specified index.
        /// </summary>
        /// <param name="i">The zero-based index of the element to get.</param>
        /// <returns>The string value.</returns>
        public string ValueAt(int i) => this.Values[i].Value;

        /// <summary>
        /// Gets the token count at the specified index.
        /// </summary>
        /// <param name="i">The zero-based index of the element to get.</param>
        /// <returns>The token count.</returns>
        public int TokenCountAt(int i) => this.Values[i].TokenCount;
    }

    /// <summary>
    /// Delegate for counting tokens in a string.
    /// </summary>
    /// <param name="input">The input string to count tokens in.</param>
    /// <returns>The number of tokens in the input string.</returns>
    public delegate int TokenCounter(string input);

    private static readonly char[] s_spaceChar = [' '];
    private static readonly string?[] s_plaintextSplitOptions = ["\n", ".。．", "?!", ";", ":", ",，、", ")]}", " ", "-", null];
    private static readonly string?[] s_markdownSplitOptions = [".\u3002\uFF0E", "?!", ";", ":", ",\uFF0C\u3001", ")]}", " ", "-", "\n\r", null];

    /// <summary>
    /// Splits plain text into lines, trimming the results.
    /// </summary>
    /// <param name="text">The raw text to split.</param>
    /// <param name="maxTokensPerLine">The maximum number of tokens allowed per line.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <returns>A list of split lines.</returns>
    public static List<string> SplitPlainTextLines(string text, int maxTokensPerLine, TokenCounter? tokenCounter = null) =>
        InternalSplitLines(text, maxTokensPerLine, trim: true, s_plaintextSplitOptions, tokenCounter);

    /// <summary>
    /// Splits markdown text into lines, trimming the results.
    /// </summary>
    /// <param name="text">The raw markdown text to split.</param>
    /// <param name="maxTokensPerLine">The maximum number of tokens allowed per line.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <returns>A list of split lines.</returns>
    public static List<string> SplitMarkDownLines(string text, int maxTokensPerLine, TokenCounter? tokenCounter = null) =>
        InternalSplitLines(text, maxTokensPerLine, trim: true, s_markdownSplitOptions, tokenCounter);

    /// <summary>
    /// Splits plain text lines into paragraphs based on maximum token constraints.
    /// </summary>
    /// <param name="lines">The collection of text lines.</param>
    /// <param name="maxTokensPerParagraph">The maximum number of tokens allowed per paragraph.</param>
    /// <param name="overlapTokens">The number of tokens to overlap between paragraphs.</param>
    /// <param name="chunkHeader">Optional text prepended to each chunk.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <returns>A list of paragraphs.</returns>
    public static List<string> SplitPlainTextParagraphs(
        IEnumerable<string> lines,
        int maxTokensPerParagraph,
        int overlapTokens = 0,
        string? chunkHeader = null,
        TokenCounter? tokenCounter = null) =>
        InternalSplitTextParagraphs(
            lines.Select(line => line
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')),
            maxTokensPerParagraph,
            overlapTokens,
            chunkHeader,
            static (text, maxTokens, tokenCounter) => InternalSplitLines(text, maxTokens, trim: false, s_plaintextSplitOptions, tokenCounter),
            tokenCounter);

    /// <summary>
    /// Splits markdown lines into paragraphs based on maximum token constraints.
    /// </summary>
    /// <param name="lines">The collection of markdown lines.</param>
    /// <param name="maxTokensPerParagraph">The maximum number of tokens allowed per paragraph.</param>
    /// <param name="overlapTokens">The number of tokens to overlap between paragraphs.</param>
    /// <param name="chunkHeader">Optional text prepended to each chunk.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <returns>A list of paragraphs.</returns>
    public static List<string> SplitMarkdownParagraphs(IEnumerable<string> lines, int maxTokensPerParagraph, int overlapTokens = 0, string? chunkHeader = null, TokenCounter? tokenCounter = null) =>
        InternalSplitTextParagraphs(lines, maxTokensPerParagraph, overlapTokens, chunkHeader, static (text, maxTokens, tokenCounter) => InternalSplitLines(text, maxTokens, trim: false, s_markdownSplitOptions, tokenCounter), tokenCounter);

    /// <summary>
    /// Internal helper that splits lines into paragraphs using paragraph token size constraints.
    /// </summary>
    /// <param name="lines">The collection of text lines.</param>
    /// <param name="maxTokensPerParagraph">The maximum number of tokens allowed per paragraph.</param>
    /// <param name="overlapTokens">The number of tokens to overlap between paragraphs.</param>
    /// <param name="chunkHeader">Optional text prepended to each chunk.</param>
    /// <param name="longLinesSplitter">A function delegate used to split lines that exceed the token limit.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <returns>A list of paragraph strings.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="maxTokensPerParagraph"/> is non-positive or less than or equal to <paramref name="overlapTokens"/>.</exception>
    private static List<string> InternalSplitTextParagraphs(IEnumerable<string> lines, int maxTokensPerParagraph, int overlapTokens, string? chunkHeader, Func<string, int, TokenCounter?, List<string>> longLinesSplitter, TokenCounter? tokenCounter)
    {
        if (maxTokensPerParagraph <= 0)
        {
            throw new ArgumentException("maxTokensPerParagraph should be a positive number", nameof(maxTokensPerParagraph));
        }

        if (maxTokensPerParagraph <= overlapTokens)
        {
            throw new ArgumentException("overlapTokens cannot be larger than maxTokensPerParagraph", nameof(maxTokensPerParagraph));
        }

        // Optimize empty inputs if we can efficiently determine they're empty
        if (lines is ICollection<string> c && c.Count == 0)
        {
            return [];
        }

        var chunkHeaderTokens = chunkHeader is { Length: > 0 } ? GetTokenCount(chunkHeader, tokenCounter) : 0;
        var adjustedMaxTokensPerParagraph = maxTokensPerParagraph - overlapTokens - chunkHeaderTokens;

        // Split long lines first
        IEnumerable<string> truncatedLines = lines.SelectMany(line => longLinesSplitter(line, adjustedMaxTokensPerParagraph, tokenCounter));

        var paragraphs = BuildParagraph(truncatedLines, adjustedMaxTokensPerParagraph, tokenCounter);
        var processedParagraphs = ProcessParagraphs(paragraphs, adjustedMaxTokensPerParagraph, overlapTokens, chunkHeader, longLinesSplitter, tokenCounter);

        return processedParagraphs;
    }

    /// <summary>
    /// Combines split lines into paragraph blocks within token constraints.
    /// </summary>
    /// <param name="truncatedLines">The collection of pre-split or truncated lines.</param>
    /// <param name="maxTokensPerParagraph">The maximum number of tokens allowed per paragraph.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <returns>A list of built paragraph strings.</returns>
    private static List<string> BuildParagraph(IEnumerable<string> truncatedLines, int maxTokensPerParagraph, TokenCounter? tokenCounter)
    {
        StringBuilder paragraphBuilder = new();
        List<string> paragraphs = [];

        foreach (string line in truncatedLines)
        {
            if (paragraphBuilder.Length > 0)
            {
                string? paragraph = null;

                int currentCount = GetTokenCount(line, tokenCounter) + 1;
                if (currentCount < maxTokensPerParagraph)
                {
                    currentCount += tokenCounter is null ?
                        GetDefaultTokenCount(paragraphBuilder.Length) :
                        tokenCounter(paragraph = paragraphBuilder.ToString());
                }

                if (currentCount >= maxTokensPerParagraph)
                {
                    // Complete the paragraph and prepare for the next
                    paragraph ??= paragraphBuilder.ToString();
                    paragraphs.Add(paragraph.Trim());
                    paragraphBuilder.Clear();
                }
            }

            paragraphBuilder.AppendLine(line);
        }

        if (paragraphBuilder.Length > 0)
        {
            // Add the final paragraph if there's anything remaining
            paragraphs.Add(paragraphBuilder.ToString().Trim());
        }

        return paragraphs;
    }

    /// <summary>
    /// Distributes paragraphs evenly and appends headers and overlapping text.
    /// </summary>
    /// <param name="paragraphs">The list of paragraphs to process.</param>
    /// <param name="adjustedMaxTokensPerParagraph">The adjusted maximum tokens per paragraph.</param>
    /// <param name="overlapTokens">The number of tokens to overlap between paragraphs.</param>
    /// <param name="chunkHeader">Optional chunk header text.</param>
    /// <param name="longLinesSplitter">A function delegate used to split lines that exceed the token limit.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <returns>A processed list of paragraph strings.</returns>
    private static List<string> ProcessParagraphs(List<string> paragraphs, int adjustedMaxTokensPerParagraph, int overlapTokens, string? chunkHeader, Func<string, int, TokenCounter?, List<string>> longLinesSplitter, TokenCounter? tokenCounter)
    {
        // distribute text more evenly in the last paragraphs when the last paragraph is too short.
        if (paragraphs.Count > 1)
        {
            var lastParagraph = paragraphs[paragraphs.Count - 1];
            var secondLastParagraph = paragraphs[paragraphs.Count - 2];

            if (GetTokenCount(lastParagraph, tokenCounter) < adjustedMaxTokensPerParagraph / 4)
            {
                var lastParagraphTokens = lastParagraph.Split(s_spaceChar, StringSplitOptions.RemoveEmptyEntries);
                var secondLastParagraphTokens = secondLastParagraph.Split(s_spaceChar, StringSplitOptions.RemoveEmptyEntries);

                var lastParagraphTokensCount = lastParagraphTokens.Length;
                var secondLastParagraphTokensCount = secondLastParagraphTokens.Length;

                if (lastParagraphTokensCount + secondLastParagraphTokensCount <= adjustedMaxTokensPerParagraph)
                {
                    var newSecondLastParagraph = string.Join(" ", secondLastParagraphTokens);
                    var newLastParagraph = string.Join(" ", lastParagraphTokens);

                    paragraphs[paragraphs.Count - 2] = $"{newSecondLastParagraph} {newLastParagraph}";
                    paragraphs.RemoveAt(paragraphs.Count - 1);
                }
            }
        }

        var processedParagraphs = new List<string>();
        var paragraphStringBuilder = new StringBuilder();

        for (int i = 0; i < paragraphs.Count; i++)
        {
            paragraphStringBuilder.Clear();

            if (chunkHeader is not null)
            {
                paragraphStringBuilder.Append(chunkHeader);
            }

            var paragraph = paragraphs[i];

            if (overlapTokens > 0 && i < paragraphs.Count - 1)
            {
                var nextParagraph = paragraphs[i + 1];
                var split = longLinesSplitter(nextParagraph, overlapTokens, tokenCounter);

                paragraphStringBuilder.Append(paragraph);

                if (split.Count != 0)
                {
                    paragraphStringBuilder.Append(' ').Append(split[0]);
                }
            }
            else
            {
                paragraphStringBuilder.Append(paragraph);
            }

            processedParagraphs.Add(paragraphStringBuilder.ToString());
        }

        return processedParagraphs;
    }

    /// <summary>
    /// Recursively splits text content into lines that conform to the token limit.
    /// </summary>
    /// <param name="text">The raw text to split.</param>
    /// <param name="maxTokensPerLine">The maximum tokens allowed per line.</param>
    /// <param name="trim"><c>true</c> to trim whitespace; otherwise, <c>false</c>.</param>
    /// <param name="splitOptions">The order of punctuation/separators to try splitting on.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <returns>A list of split line strings.</returns>
    private static List<string> InternalSplitLines(string text, int maxTokensPerLine, bool trim, string?[] splitOptions, TokenCounter? tokenCounter)
    {
        var result = new StringListWithTokenCount(tokenCounter);

        text = text.Replace("\r\n", "\n"); // normalize line endings
        result.Add(text);
        for (int i = 0; i < splitOptions.Length; i++)
        {
            int count = result.Count; // track where the original input left off
            var (splits2, inputWasSplit2) = Split(result, maxTokensPerLine, splitOptions[i].AsSpan(), trim, tokenCounter);
            result.AddRange(splits2);
            result.RemoveRange(0, count); // remove the original input
            if (!inputWasSplit2)
            {
                break;
            }
        }
        return result.ToStringList();
    }

    /// <summary>
    /// Iterates through the list of input strings and splits any that exceed token limits.
    /// </summary>
    /// <param name="input">The collection of input strings with token counts.</param>
    /// <param name="maxTokens">The maximum tokens allowed per item.</param>
    /// <param name="separators">The separator characters to split on.</param>
    /// <param name="trim"><c>true</c> to trim whitespace; otherwise, <c>false</c>.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <returns>A tuple containing the split string list and a boolean indicating if any splits were made.</returns>
    private static (StringListWithTokenCount, bool) Split(StringListWithTokenCount input, int maxTokens, ReadOnlySpan<char> separators, bool trim, TokenCounter? tokenCounter)
    {
        bool inputWasSplit = false;
        StringListWithTokenCount result = new(tokenCounter);
        int count = input.Count;
        for (int i = 0; i < count; i++)
        {
            var (splits, split) = Split(input.ValueAt(i).AsSpan(), input.ValueAt(i), maxTokens, separators, trim, tokenCounter, input.TokenCountAt(i));
            result.AddRange(splits);
            inputWasSplit |= split;
        }
        return (result, inputWasSplit);
    }

    /// <summary>
    /// Splits a single span/string that exceeds the token count limit on separator characters.
    /// </summary>
    /// <param name="input">The span representing the text to split.</param>
    /// <param name="inputString">The original input string (if materialized).</param>
    /// <param name="maxTokens">The maximum tokens allowed per item.</param>
    /// <param name="separators">The separator characters to split on.</param>
    /// <param name="trim"><c>true</c> to trim whitespace; otherwise, <c>false</c>.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <param name="inputTokenCount">The current token count of the input.</param>
    /// <returns>A tuple containing the split string list and a boolean indicating if a split was made.</returns>
    /// <exception cref="Exception">Thrown if the input string does not match the span.</exception>
    private static (StringListWithTokenCount, bool) Split(ReadOnlySpan<char> input, string? inputString, int maxTokens, ReadOnlySpan<char> separators, bool trim, TokenCounter? tokenCounter, int inputTokenCount)
    {
        if (inputString is not null && !input.SequenceEqual(inputString.AsSpan())) { throw new Exception("inputString should be null or match input"); }

        StringListWithTokenCount result = new(tokenCounter);
        var inputWasSplit = false;

        if (inputTokenCount > maxTokens)
        {
            inputWasSplit = true;

            int half = input.Length / 2;
            int cutPoint = -1;

            if (separators.IsEmpty)
            {
                cutPoint = half;
            }
            else if (input.Length > 2)
            {
                int pos = 0;
                while (true)
                {
                    int index = input.Slice(pos, input.Length - 1 - pos).IndexOfAny(separators);
                    if (index < 0)
                    {
                        break;
                    }

                    index += pos;

                    if (Math.Abs(half - index) < Math.Abs(half - cutPoint))
                    {
                        cutPoint = index + 1;
                    }

                    pos = index + 1;
                }
            }

            if (cutPoint > 0)
            {
                var firstHalf = input.Slice(0, cutPoint);
                var secondHalf = input.Slice(cutPoint);
                if (trim)
                {
                    firstHalf = firstHalf.Trim();
                    secondHalf = secondHalf.Trim();
                }

                // Recursion
                var (splits1, split1) = Split(firstHalf, null, maxTokens, separators, trim, tokenCounter, GetTokenCount(firstHalf.ToString(), tokenCounter));
                result.AddRange(splits1);
                var (splits2, split2) = Split(secondHalf, null, maxTokens, separators, trim, tokenCounter, GetTokenCount(secondHalf.ToString(), tokenCounter));
                result.AddRange(splits2);

                inputWasSplit = split1 || split2;
                return (result, inputWasSplit);
            }
        }

        var resultString = inputString ?? input.ToString();
        var resultTokenCount = inputTokenCount;
        if (trim && !resultString.Trim().Equals(resultString, StringComparison.Ordinal))
        {
            resultString = resultString.Trim();
            resultTokenCount = GetTokenCount(resultString, tokenCounter);
        }

        result.Add(resultString, resultTokenCount);

        return (result, inputWasSplit);
    }

    /// <summary>
    /// Gets the token count for the specified input string.
    /// </summary>
    /// <param name="input">The input string.</param>
    /// <param name="tokenCounter">Optional delegate for counting tokens.</param>
    /// <returns>The calculated number of tokens.</returns>
    private static int GetTokenCount(string input, TokenCounter? tokenCounter) => tokenCounter is null ? GetDefaultTokenCount(input.Length) : tokenCounter(input);

    /// <summary>
    /// Default token count calculation using character length division heuristic (characters divided by 4).
    /// </summary>
    /// <param name="length">The character length.</param>
    /// <returns>The approximate token count.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="length"/> is negative.</exception>
    private static int GetDefaultTokenCount(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "Length must be non-negative.");
        return length >> 2;
    }
}
