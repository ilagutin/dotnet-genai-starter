using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Generation.Chat;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Evaluations;
using GenAIPlatform.Domain.Observability;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Evaluations.StartRun.Context;

internal sealed class EvaluationRetrievalContextBuilder(
    IEmbeddingClient embeddingClient,
    IRagVectorSearchStore vectorSearchStore,
    TimeProvider timeProvider,
    IOptions<RagOptions> ragOptions,
    RagPromptBuilder promptBuilder)
{
    private int MaxContextCharacters => Math.Max(1, ragOptions.Value.MaxContextCharacters);

    public async Task<EvaluationRetrievalContext> BuildAsync(
        EvaluationCase evaluationCase,
        ModelGatewayRequestSettings gateway,
        EvaluationRetrievalConfiguration retrievalConfig,
        string tenantId,
        string userId,
        CancellationToken cancellationToken)
    {
        var message = evaluationCase.Question.Trim();
        if (!string.IsNullOrWhiteSpace(evaluationCase.Context))
        {
            return new EvaluationRetrievalContext(
                message,
                TrimContext(evaluationCase.Context),
                Chunks: [],
                RetrievalLatency: TimeSpan.Zero,
                Embedding: null,
                RetrievedDocuments: []);
        }

        await vectorSearchStore.CheckReadinessAsync(cancellationToken);
        var retrievalStarted = timeProvider.GetTimestamp();
        var embedding = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest(
                message,
                retrievalConfig.EmbeddingModel,
                $"{gateway.CorrelationId}-{evaluationCase.Id}"),
            cancellationToken);
        EmbeddingVectorValidator.EnsureValidCosineVector(embedding);
        var chunks = await vectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                embedding.Vector,
                embedding.Model,
                embedding.Provider,
                tenantId,
                userId,
                retrievalConfig.TopK,
                retrievalConfig.MinSimilarityScore,
                DocumentIds: []),
            cancellationToken);

        var promptContext = BuildPromptContext(chunks);

        return new EvaluationRetrievalContext(
            message,
            promptContext.ContextText,
            chunks,
            timeProvider.GetElapsedTime(retrievalStarted),
            embedding,
            promptContext.RetrievedDocuments);
    }

    private EvaluationPromptContext BuildPromptContext(IReadOnlyList<RetrievedDocumentChunk> chunks)
    {
        var ragContext = promptBuilder.Build(chunks, MaxContextCharacters);

        return new EvaluationPromptContext(
            ragContext.ContextText,
            ragContext.Citations
                .Select(static citation => new RetrievedDocumentReference(
                    citation.ReferenceId,
                    citation.DocumentId,
                    citation.ChunkId))
                .ToArray());
    }

    private string TrimContext(string context)
    {
        var trimmed = context.Trim();
        return trimmed.Length <= MaxContextCharacters
            ? trimmed
            : trimmed[..MaxContextCharacters].TrimEnd();
    }
}
