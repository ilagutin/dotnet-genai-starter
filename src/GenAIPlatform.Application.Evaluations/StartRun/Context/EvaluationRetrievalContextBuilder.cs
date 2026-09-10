using System.Text;
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
    IOptions<RagOptions> ragOptions)
{
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

        var promptContext = BuildPromptContext(evaluationCase, chunks);

        return new EvaluationRetrievalContext(
            message,
            promptContext.ContextText,
            chunks,
            timeProvider.GetElapsedTime(retrievalStarted),
            embedding,
            promptContext.RetrievedDocuments);
    }

    private EvaluationPromptContext BuildPromptContext(
        EvaluationCase evaluationCase,
        IReadOnlyList<RetrievedDocumentChunk> chunks)
    {
        var context = evaluationCase.Context;
        if (!string.IsNullOrWhiteSpace(context))
        {
            return new EvaluationPromptContext(
                TrimContext(context),
                RetrievedDocuments: []);
        }

        return BuildContext(chunks);
    }

    private EvaluationPromptContext BuildContext(IReadOnlyList<RetrievedDocumentChunk> chunks)
    {
        if (chunks.Count == 0)
        {
            return new EvaluationPromptContext(
                ContextText: string.Empty,
                RetrievedDocuments: []);
        }

        var maxCharacters = Math.Max(1, ragOptions.Value.MaxContextCharacters);
        var builder = new StringBuilder(maxCharacters);
        var includedDocuments = new List<RetrievedDocumentReference>();

        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            var chunkText = $"[{index + 1}] {chunk.Title}\n{chunk.Text}";
            var separator = builder.Length == 0 ? string.Empty : "\n\n";
            var remainingCharacters = maxCharacters - builder.Length - separator.Length;
            if (remainingCharacters <= 0)
            {
                break;
            }

            if (separator.Length > 0)
            {
                builder.Append(separator);
            }

            var charactersToAppend = Math.Min(chunkText.Length, remainingCharacters);
            builder.Append(chunkText, 0, charactersToAppend);
            includedDocuments.Add(ToRetrievedDocumentReference(chunk, index));

            if (charactersToAppend < chunkText.Length)
            {
                break;
            }
        }

        return new EvaluationPromptContext(
            builder.ToString().TrimEnd(),
            includedDocuments);
    }

    private string TrimContext(string context)
    {
        var maxCharacters = Math.Max(1, ragOptions.Value.MaxContextCharacters);
        var trimmed = context.Trim();
        return trimmed.Length <= maxCharacters
            ? trimmed
            : trimmed[..maxCharacters].TrimEnd();
    }

    private static RetrievedDocumentReference ToRetrievedDocumentReference(
        RetrievedDocumentChunk chunk,
        int index)
    {
        return new RetrievedDocumentReference(
            (index + 1).ToString(),
            chunk.DocumentId,
            chunk.ChunkId);
    }
}
