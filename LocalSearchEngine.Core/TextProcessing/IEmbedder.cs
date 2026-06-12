using SmartComponents.LocalEmbeddings;

namespace LocalSearchEngine.Core.TextProcessing;

/// <summary>
/// Defines a contract for generating vector embeddings from text inputs.
/// </summary>
public interface IEmbedder
{
    /// <summary>
    /// Generates a vector embedding for a passage or document text block.
    /// </summary>
    /// <param name="text">The text block to embed.</param>
    /// <returns>A read-only memory block containing the embedding vector components.</returns>
    ReadOnlyMemory<float> Embed(string text);

    /// <summary>
    /// Generates a vector embedding for a search query.
    /// </summary>
    /// <param name="text">The query text to embed.</param>
    /// <returns>A read-only memory block containing the embedding vector components.</returns>
    ReadOnlyMemory<float> EmbedQuery(string text);
}

/// <summary>
/// Adapts a local embedding model to the <see cref="IEmbedder"/> interface.
/// </summary>
public sealed class LocalEmbedderAdapter : IEmbedder, IDisposable
{
    /// <summary>
    /// The name of the local embedding model files.
    /// </summary>
    public const string ModelName = "snowflake-arctic-embed-s";

    /// <summary>
    /// The retrieval instruction prefix prepended to search queries.
    /// </summary>
    public const string QueryInstruction = "query: ";

    private readonly LocalEmbedder _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalEmbedderAdapter"/> class using the default model name.
    /// </summary>
    public LocalEmbedderAdapter() : this(new LocalEmbedder(ModelName)) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalEmbedderAdapter"/> class wrapping a custom <see cref="LocalEmbedder"/>.
    /// </summary>
    /// <param name="inner">The underlying local embedder engine.</param>
    public LocalEmbedderAdapter(LocalEmbedder inner) => _inner = inner;

    /// <summary>
    /// Generates a vector embedding for the given passage.
    /// </summary>
    /// <param name="text">The text block to embed.</param>
    /// <returns>A read-only memory block containing the embedding vector.</returns>
    public ReadOnlyMemory<float> Embed(string text) => _inner.Embed(text).Values;

    /// <summary>
    /// Generates a vector embedding for the given query, prepending the query instruction.
    /// </summary>
    /// <param name="text">The query text to embed.</param>
    /// <returns>A read-only memory block containing the embedding vector.</returns>
    public ReadOnlyMemory<float> EmbedQuery(string text) => _inner.Embed(QueryInstruction + text).Values;

    /// <summary>
    /// Disposes the resources used by the local embedder adapter.
    /// </summary>
    public void Dispose() => _inner.Dispose();
}
