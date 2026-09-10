using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Evaluations.Retrieval;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// Runs one baseline query through the same permission-aware retrieval port the RAG chat
/// path uses, so the baseline measures production retrieval rather than a copy of it.
/// </summary>
internal sealed class RetrievalBaselineQueryExecutor(
    IEmbeddingClient embeddingClient,
    IRagVectorSearchStore vectorSearchStore,
    TimeProvider timeProvider)
{
    public async Task<RetrievalBaselineQueryExecution> ExecuteAsync(
        RetrievalBaselineQuery query,
        RetrievalBaselineSearchSettings settings,
        IReadOnlyDictionary<Guid, string> documentIdsByRowId,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var embedding = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest(query.Question, settings.EmbeddingModel, $"retrieval-baseline-{query.Id}"),
            cancellationToken);
        EmbeddingVectorValidator.EnsureValidCosineVector(embedding);

        var chunks = await vectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                embedding.Vector,
                embedding.Model,
                embedding.Provider,
                query.TenantId,
                query.UserId,
                settings.TopK,
                settings.MinSimilarityScore,
                DocumentIds: []),
            cancellationToken);
        var elapsed = timeProvider.GetElapsedTime(started);

        return new RetrievalBaselineQueryExecution(
            MapDocumentIds(chunks, documentIdsByRowId),
            elapsed.TotalMilliseconds);
    }

    private static IReadOnlyList<string> MapDocumentIds(
        IReadOnlyList<RetrievedDocumentChunk> chunks,
        IReadOnlyDictionary<Guid, string> documentIdsByRowId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var documentIds = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            // A row outside the benchmark corpus would be a retrieval leak, so it is
            // reported by its raw row id instead of being dropped.
            var documentId = documentIdsByRowId.TryGetValue(chunk.DocumentId, out var known)
                ? known
                : $"unknown-document-{chunk.DocumentId:n}";
            if (seen.Add(documentId))
            {
                documentIds.Add(documentId);
            }
        }

        return documentIds;
    }
}
