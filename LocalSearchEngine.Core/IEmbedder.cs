using SmartComponents.LocalEmbeddings;

namespace LocalSearchEngine.Core;

/// <summary>
/// Turns text into an embedding vector. A seam over <see cref="LocalEmbedder"/> so
/// the search/index pipeline can be exercised in tests without the ONNX model.
/// </summary>
public interface IEmbedder
{
    /// <summary>Embeds a passage/document for indexing.</summary>
    ReadOnlyMemory<float> Embed(string text);

    /// <summary>
    /// Embeds a search query. BGE is an asymmetric retrieval model: queries must be
    /// prefixed with a short instruction while passages are embedded raw, so this is a
    /// distinct operation from <see cref="Embed"/>.
    /// </summary>
    ReadOnlyMemory<float> EmbedQuery(string text);
}

/// <summary>Adapts SmartComponents' <see cref="LocalEmbedder"/> to <see cref="IEmbedder"/>.</summary>
public sealed class LocalEmbedderAdapter : IEmbedder, IDisposable
{
    /// <summary>
    /// The local embedding model. Must match the <c>LocalEmbeddingsModelName</c> MSBuild
    /// property in Directory.Build.props — that is the folder the model is copied into at
    /// build time. bge-small-en-v1.5 is 384-dimensional, matching
    /// <see cref="TextChunkRecord.Embedding"/>; re-index if you change it.
    /// </summary>
    public const string ModelName = "bge-small-en-v1.5";

    /// <summary>
    /// The retrieval instruction bge-*-en-v1.5 expects prepended to queries (and only
    /// queries). Embedding the query without it measurably hurts recall for this model
    /// family; see the model card.
    /// </summary>
    public const string QueryInstruction = "Represent this sentence for searching relevant passages: ";

    private readonly LocalEmbedder _inner;

    public LocalEmbedderAdapter() : this(new LocalEmbedder(ModelName)) { }

    public LocalEmbedderAdapter(LocalEmbedder inner) => _inner = inner;

    public ReadOnlyMemory<float> Embed(string text) => _inner.Embed(text).Values;

    public ReadOnlyMemory<float> EmbedQuery(string text) => _inner.Embed(QueryInstruction + text).Values;

    public void Dispose() => _inner.Dispose();
}
