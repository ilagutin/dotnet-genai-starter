using System.Text.Json;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Infrastructure.Embeddings.OpenAi.Dtos;

namespace GenAIPlatform.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingResponseMapper
{
    public EmbeddingResponse Map(
        string responseContent,
        EmbeddingRequest request,
        OpenAiEmbeddingErrorMapper errorMapper)
    {
        var embeddingResponse = JsonSerializer.Deserialize<OpenAiEmbeddingResponse>(
            responseContent,
            OpenAiEmbeddingJson.Options);
        var embedding = embeddingResponse?.Data?.FirstOrDefault()?.Embedding;
        if (embedding is null || embedding.Count == 0)
        {
            throw errorMapper.EmptyEmbedding();
        }

        return new EmbeddingResponse(
            embedding,
            embeddingResponse?.Model ?? request.Model,
            OpenAiEmbeddingProvider.Name,
            embeddingResponse?.Usage?.PromptTokens,
            request.CorrelationId);
    }
}
