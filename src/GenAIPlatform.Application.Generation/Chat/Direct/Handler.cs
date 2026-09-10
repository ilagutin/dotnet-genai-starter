using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Application.Generation.Prompts.Templates;

namespace GenAIPlatform.Application.Generation.Chat;

public sealed class DirectChatHandler(
    IAiModelClient modelClient,
    IPromptRenderer promptRenderer,
    ModelGatewayRequestPolicy modelGatewayRequestPolicy,
    IAiModelRequestLogger requestLogger)
    : IRequestHandler<DirectChatCommand, DirectChatResponse>
{
    public async Task<DirectChatResponse> HandleAsync(
        DirectChatCommand request,
        CancellationToken cancellationToken)
    {
        var message = request.Message.Trim();

        var modelGatewayRequest = modelGatewayRequestPolicy.Resolve(
            request.Model,
            request.Temperature,
            request.MaxOutputTokens,
            request.CorrelationId);

        var renderedPrompt = await promptRenderer.RenderActiveAsync(
            DirectChatPrompt.TemplateName,
            new Dictionary<string, string>
            {
                ["message"] = message
            },
            cancellationToken);

        var messages = new[]
        {
            new AiChatMessage(AiMessageRole.System, renderedPrompt.SystemMessage),
            new AiChatMessage(AiMessageRole.User, renderedPrompt.UserMessage)
        };
        modelGatewayRequestPolicy.ValidateInputMessages(messages);

        var aiRequest = new AiModelRequest(
            CorrelationId: modelGatewayRequest.CorrelationId,
            Model: modelGatewayRequest.Model,
            Messages: messages,
            Temperature: modelGatewayRequest.Temperature,
            MaxOutputTokens: modelGatewayRequest.MaxOutputTokens,
            Prompt: renderedPrompt.Metadata);

        var aiResponse = await requestLogger.CompleteAndLogAsync(
            modelClient,
            aiRequest,
            retrievalLatency: null,
            embeddingTokens: null,
            embeddingProvider: null,
            embeddingModel: null,
            retrievedDocuments: [],
            cancellationToken);

        return new DirectChatResponse(
            Message: aiResponse.Content,
            Model: aiResponse.Model,
            Provider: aiResponse.Provider,
            Usage: aiResponse.Usage,
            Prompt: renderedPrompt.Metadata,
            CorrelationId: modelGatewayRequest.CorrelationId);
    }
}
