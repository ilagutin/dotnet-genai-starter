using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;

namespace GenAIPlatform.Application.Generation.Chat;

internal sealed record RagRetrievalResult(
    EmbeddingResponse Embedding,
    IReadOnlyList<RetrievedDocumentChunk> Chunks,
    TimeSpan RetrievalLatency);
