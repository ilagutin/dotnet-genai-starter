using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Generation.Chat;

internal sealed class RagRetrievalPipeline(
    IEmbeddingClient embeddingClient,
    IRagVectorSearchStore vectorSearchStore,
    TimeProvider timeProvider,
    IOptions<EmbeddingOptions> embeddingOptions)
{
    public async Task<RagRetrievalResult> RetrieveAsync(
        string message,
        string tenantId,
        string userId,
        RagChatValidationResult validatedRequest,
        ModelGatewayRequestSettings modelGatewayRequest,
        CancellationToken cancellationToken)
    {
        await vectorSearchStore.CheckReadinessAsync(cancellationToken);

        var retrievalStarted = timeProvider.GetTimestamp();
        var embeddingResponse = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest(
                message,
                embeddingOptions.Value.DefaultModel,
                modelGatewayRequest.CorrelationId),
            cancellationToken);

        EmbeddingVectorValidator.EnsureValidCosineVector(embeddingResponse);

        var chunks = await vectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                embeddingResponse.Vector,
                embeddingResponse.Model,
                embeddingResponse.Provider,
                tenantId,
                userId,
                validatedRequest.TopK,
                validatedRequest.MinSimilarityScore,
                validatedRequest.DocumentIds),
            cancellationToken);

        return new RagRetrievalResult(
            embeddingResponse,
            chunks,
            timeProvider.GetElapsedTime(retrievalStarted));
    }
}
