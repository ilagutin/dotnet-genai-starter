using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Application.Generation.Prompts.Templates;

namespace GenAIPlatform.Application.Agentic.Chat;

internal sealed class AgenticPromptBuilder(
    IPromptRenderer promptRenderer,
    ModelGatewayRequestPolicy modelGatewayRequestPolicy)
{
    public async Task<AgenticPromptMessages> CreateInitialPromptAsync(
        string message,
        CancellationToken cancellationToken)
    {
        var renderedPrompt = await promptRenderer.RenderActiveAsync(
            AgenticChatPrompt.TemplateName,
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
        return new AgenticPromptMessages(messages, renderedPrompt.Metadata);
    }
}
