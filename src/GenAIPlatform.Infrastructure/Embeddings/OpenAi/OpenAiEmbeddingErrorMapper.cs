using System.Net;
using System.Text.Json;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Infrastructure.Embeddings.OpenAi.Dtos;

namespace GenAIPlatform.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingErrorMapper
{
    public EmbeddingClientException FromHttpFailure(
        HttpStatusCode statusCode,
        string responseContent)
    {
        var providerError = TryReadError(responseContent);
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            providerError?.Error?.Message
                ?? $"Embedding provider returned HTTP {(int)statusCode}.",
            NormalizeProviderErrorCode(statusCode),
            statusCode,
            providerError?.Error?.Code);
    }

    public EmbeddingClientException EmptyEmbedding()
    {
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            "Embedding provider returned no embedding vector.",
            errorCode: "empty_embedding");
    }

    public EmbeddingClientException Timeout(TaskCanceledException exception)
    {
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            "Embedding provider request timed out.",
            errorCode: "timeout",
            innerException: exception);
    }

    public EmbeddingClientException Transport(HttpRequestException exception)
    {
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            "Embedding provider request failed before a valid response was received.",
            errorCode: "transport_error",
            statusCode: exception.StatusCode,
            innerException: exception);
    }

    public EmbeddingClientException InvalidJson(JsonException exception)
    {
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            "Embedding provider returned an invalid JSON response.",
            errorCode: "invalid_json",
            innerException: exception);
    }

    private static string NormalizeProviderErrorCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => "invalid_request",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_error",
            HttpStatusCode.RequestTimeout => "provider_timeout",
            HttpStatusCode.TooManyRequests => "rate_limited",
            _ when (int)statusCode >= 500 => "provider_unavailable",
            _ => "provider_error"
        };
    }

    private static OpenAiErrorResponse? TryReadError(string responseContent)
    {
        try
        {
            return JsonSerializer.Deserialize<OpenAiErrorResponse>(
                responseContent,
                OpenAiEmbeddingJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
