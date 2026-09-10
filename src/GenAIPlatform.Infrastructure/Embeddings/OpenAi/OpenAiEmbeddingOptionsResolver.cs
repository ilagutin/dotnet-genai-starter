using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingOptionsResolver(
    IOptions<OpenAiCompatibleEmbeddingClientOptions> options)
{
    public OpenAiCompatibleEmbeddingClientOptions Get()
    {
        try
        {
            return options.Value;
        }
        catch (OptionsValidationException exception)
        {
            throw new EmbeddingClientException(
                OpenAiEmbeddingProvider.Name,
                "OpenAI-compatible embedding provider configuration is invalid.",
                errorCode: "configuration_error",
                innerException: exception);
        }
    }

    public Uri GetEndpointUri(OpenAiCompatibleEmbeddingClientOptions clientOptions)
    {
        if (!clientOptions.IsValid() ||
            !clientOptions.TryCreateEndpointUri(out var endpointUri) ||
            endpointUri is null)
        {
            throw new EmbeddingClientException(
                OpenAiEmbeddingProvider.Name,
                "OpenAI-compatible embedding provider configuration is invalid.",
                errorCode: "configuration_error");
        }

        return endpointUri;
    }
}
