using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Infrastructure.Configuration;
using GenAIPlatform.Infrastructure.Embeddings.OpenAi.Dtos;

namespace GenAIPlatform.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingRequestFactory
{
    public string CreatePayloadJson(EmbeddingRequest request)
    {
        return JsonSerializer.Serialize(
            new OpenAiEmbeddingRequest(request.Model, request.Input),
            OpenAiEmbeddingJson.Options);
    }

    public HttpRequestMessage CreateHttpRequest(
        OpenAiCompatibleEmbeddingClientOptions clientOptions,
        EmbeddingRequest request,
        string payloadJson,
        Uri endpointUri)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clientOptions.ApiKey);

        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            httpRequest.Headers.Add("X-Correlation-Id", request.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(clientOptions.Organization))
        {
            httpRequest.Headers.Add("OpenAI-Organization", clientOptions.Organization);
        }

        httpRequest.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        return httpRequest;
    }
}
