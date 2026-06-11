using SmartComponents.LocalEmbeddings;

namespace LocalSearchEngine.Core;

/// <summary>
/// Turns text into an embedding vector. A seam over <see cref="LocalEmbedder"/> so
/// the search/index pipeline can be exercised in tests without the ONNX model.
/// </summary>
public interface IEmbedder
{
    ReadOnlyMemory<float> Embed(string text);
}

/// <summary>Adapts SmartComponents' <see cref="LocalEmbedder"/> to <see cref="IEmbedder"/>.</summary>
public sealed class LocalEmbedderAdapter : IEmbedder, IDisposable
{
    private readonly LocalEmbedder _inner;

    public LocalEmbedderAdapter(LocalEmbedder inner) => _inner = inner;

    public ReadOnlyMemory<float> Embed(string text) => _inner.Embed(text).Values;

    public void Dispose() => _inner.Dispose();
}
