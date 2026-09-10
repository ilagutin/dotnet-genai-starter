using System.Net;
using System.Text.Json;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Infrastructure.ModelGateway.OpenAi.Dtos;

namespace GenAIPlatform.Infrastructure.ModelGateway.OpenAi;

internal sealed class OpenAiModelErrorMapper
{
    public AiModelException FromHttpFailure(
        HttpStatusCode statusCode,
        string responseContent)
    {
        var providerError = TryReadError(responseContent);
        return new AiModelException(
            OpenAiModelProvider.Name,
            providerError?.Error?.Message
                ?? $"Model provider returned HTTP {(int)statusCode}.",
            NormalizeProviderErrorCode(statusCode),
            statusCode,
            providerError?.Error?.Code);
    }

    public AiModelException Timeout(TaskCanceledException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider request timed out.",
            errorCode: "timeout",
            innerException: exception);
    }

    public AiModelException Transport(HttpRequestException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider request failed before a valid response was received.",
            errorCode: "transport_error",
            statusCode: exception.StatusCode,
            innerException: exception);
    }

    public AiModelException InvalidJson(JsonException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider returned an invalid JSON response.",
            errorCode: "invalid_json",
            innerException: exception);
    }

    public AiModelException EmptyResponse()
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider returned no chat completion content.",
            errorCode: "empty_response");
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
                OpenAiModelJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
