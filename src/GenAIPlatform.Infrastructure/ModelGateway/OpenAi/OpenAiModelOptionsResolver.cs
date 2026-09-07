using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.ModelGateway.OpenAi;

internal sealed class OpenAiModelOptionsResolver(
    IOptions<OpenAiCompatibleModelClientOptions> options)
{
    public OpenAiCompatibleModelClientOptions Get()
    {
        try
        {
            return options.Value;
        }
        catch (OptionsValidationException exception)
        {
            throw new AiModelException(
                OpenAiModelProvider.Name,
                "OpenAI-compatible model provider configuration is invalid.",
                errorCode: "configuration_error",
                innerException: exception);
        }
    }

    public Uri GetEndpointUri(OpenAiCompatibleModelClientOptions clientOptions)
    {
        if (!clientOptions.IsValid() ||
            !clientOptions.TryCreateEndpointUri(out var endpointUri) ||
            endpointUri is null)
        {
            throw new AiModelException(
                OpenAiModelProvider.Name,
                "OpenAI-compatible model provider configuration is invalid.",
                errorCode: "configuration_error");
        }

        return endpointUri;
    }
}
