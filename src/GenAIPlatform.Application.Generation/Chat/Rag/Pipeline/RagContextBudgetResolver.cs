using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Application.Generation.Prompts.Templates;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Generation.Chat;

internal sealed class RagContextBudgetResolver(
    IPromptRenderer promptRenderer,
    ModelGatewayRequestPolicy modelGatewayRequestPolicy,
    IOptions<RagOptions> ragOptions)
{
    public async Task<int> ResolveAsync(
        string message,
        CancellationToken cancellationToken)
    {
        var emptyContextPrompt = await promptRenderer.RenderActiveAsync(
            RagChatPrompt.TemplateName,
            new Dictionary<string, string>
            {
                ["question"] = message,
                ["context"] = string.Empty
            },
            cancellationToken);

        var remainingCharacters =
            modelGatewayRequestPolicy.GetMaxInputMessageCharacters() -
            CountModelInputCharacters(emptyContextPrompt);

        if (remainingCharacters <= 0)
        {
            throw new ModelRequestValidationException(
                "RAG question leaves no room for document context within the configured input limit.");
        }

        return Math.Min(
            Math.Max(1, ragOptions.Value.MaxContextCharacters),
            remainingCharacters);
    }

    private static int CountModelInputCharacters(RenderedPrompt prompt)
    {
        return prompt.SystemMessage.Length + prompt.UserMessage.Length;
    }
}
