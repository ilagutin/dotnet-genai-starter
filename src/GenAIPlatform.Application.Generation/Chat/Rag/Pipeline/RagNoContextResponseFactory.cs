using GenAIPlatform.Application.Generation.ModelGateway;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Generation.Chat;

internal sealed class RagNoContextResponseFactory(IOptions<RagOptions> ragOptions)
{
    public RagChatResponse Create(ModelGatewayRequestSettings modelGatewayRequest)
    {
        return new RagChatResponse(
            NormalizeFallbackMessage(ragOptions.Value.NoContextFallbackMessage),
            modelGatewayRequest.Model,
            Provider: null,
            Usage: null,
            Prompt: null,
            modelGatewayRequest.CorrelationId,
            NoContext: true,
            Citations: []);
    }

    private static string NormalizeFallbackMessage(string? configuredMessage)
    {
        return string.IsNullOrWhiteSpace(configuredMessage)
            ? "I could not find relevant document context for that question."
            : configuredMessage.Trim();
    }
}
