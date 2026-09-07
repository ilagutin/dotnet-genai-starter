using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Observability;

namespace GenAIPlatform.Application.Evaluations.StartRun.Context;

internal sealed record EvaluationRetrievalContext(
    string Message,
    string ContextText,
    IReadOnlyList<RetrievedDocumentChunk> Chunks,
    TimeSpan RetrievalLatency,
    EmbeddingResponse? Embedding,
    IReadOnlyList<RetrievedDocumentReference> RetrievedDocuments);
