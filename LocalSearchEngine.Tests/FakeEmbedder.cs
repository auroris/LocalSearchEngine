using System;
using LocalSearchEngine.Core.TextProcessing;

namespace LocalSearchEngine.Tests;

/// <summary>
/// Deterministic, model-free embedder for tests: identical text yields an identical vector.
/// Eliminates the need to download the real model or run ONNX runtime in tests, and tracks embed counts.
/// </summary>
public class FakeEmbedder : IEmbedder
{
    public int EmbedCount { get; private set; }

    // No asymmetric query instruction in the fake, so a query equal to a chunk's text
    // reproduces that chunk's vector exactly (the self-match test depends on this).
    public virtual ReadOnlyMemory<float> EmbedQuery(string text) => EmbedInternal(text);

    public virtual ReadOnlyMemory<float> Embed(string text)
    {
        EmbedCount++;
        return EmbedInternal(text);
    }

    private static ReadOnlyMemory<float> EmbedInternal(string text)
    {
        // FNV-1a hash -> LCG fill, so the result is stable across runs and processes.
        ulong hash = 1469598103934665603UL;
        foreach (char ch in text)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }

        var vector = new float[384];
        ulong state = hash | 1UL;
        double sumSq = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            double value = ((state >> 33) / (double)(1UL << 31)) - 1.0; // ~[-1, 1]
            vector[i] = (float)value;
            sumSq += value * value;
        }

        double norm = Math.Sqrt(sumSq);
        if (norm > 0)
        {
            for (int i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        }
        return vector;
    }
}
