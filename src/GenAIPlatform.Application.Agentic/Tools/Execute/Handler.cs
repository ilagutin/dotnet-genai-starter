using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools.Execution;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Security;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Agentic.Tools.Execute;

internal sealed class ExecuteToolHandler(
    IAgentToolRegistry toolRegistry,
    IUserContext userContext,
    IOptions<AgenticChatOptions> options,
    GovernedAgentToolExecutor toolExecutor)
    : IRequestHandler<ExecuteToolCommand, ExecuteToolResponse>
{
    public async Task<ExecuteToolResponse> HandleAsync(
        ExecuteToolCommand request,
        CancellationToken cancellationToken)
    {
        var context = new AgentToolExecutionContext(
            Guid.NewGuid(),
            userContext.RequireAuthenticatedTenant(),
            userContext.RequireAuthenticatedUser(),
            $"tools-execute-{Guid.NewGuid():N}",
            options.Value.PolicyVersion,
            ApproveRiskyTools: false);
        var result = await toolExecutor.ExecuteAsync(
            new AgentToolExecutionRequest(
                $"direct-{Guid.NewGuid():N}",
                request.ToolName!.Trim(),
                request.SchemaVersion,
                NormalizeArguments(request.Arguments),
                toolRegistry.GetAvailableTools(),
                context),
            cancellationToken);

        return new ExecuteToolResponse(
            result.ToolCallId,
            result.ToolName,
            result.ResponseSchemaVersion,
            result.Policy.Decision,
            result.ExecutionStatus,
            result.ResultText,
            result.ErrorCode,
            result.ErrorMessage);
    }

    private static JsonElement NormalizeArguments(JsonElement arguments)
    {
        return arguments.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new { })
            : arguments;
    }
}
