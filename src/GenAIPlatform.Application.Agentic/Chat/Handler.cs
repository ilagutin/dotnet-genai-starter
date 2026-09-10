using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Generation.ModelGateway;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Agentic.Chat;

internal sealed class AgenticChatHandler(
    IAgentToolRegistry toolRegistry,
    ModelGatewayRequestPolicy modelGatewayRequestPolicy,
    IOptions<AgenticChatOptions> options,
    IUserContext userContext,
    AgenticPromptBuilder promptBuilder,
    AgenticChatLoopRunner loopRunner)
    : IRequestHandler<AgenticChatCommand, AgenticChatResponse>
{
    public async Task<AgenticChatResponse> HandleAsync(
        AgenticChatCommand request,
        CancellationToken cancellationToken)
    {
        var message = request.Message!.Trim();
        var tenantId = userContext.RequireAuthenticatedTenant();
        var userId = userContext.RequireAuthenticatedUser();
        var settings = modelGatewayRequestPolicy.Resolve(
            request.Model,
            request.Temperature,
            request.MaxOutputTokens,
            request.CorrelationId);
        var prompt = await promptBuilder.CreateInitialPromptAsync(
            message,
            cancellationToken);
        var session = new AgenticChatSession(
            Guid.NewGuid(),
            tenantId,
            userId,
            settings,
            options.Value,
            toolRegistry.GetAvailableTools(),
            prompt,
            request.ApproveRiskyTools);

        return await loopRunner.RunAsync(
            session,
            cancellationToken);
    }
}
