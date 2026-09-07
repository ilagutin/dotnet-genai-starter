using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Tools.Execution;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Chat;

internal sealed class AgentToolAuditWriter(
    AgentToolAuditLogWriter auditLogWriter,
    ToolPolicy toolPolicy)
{
    public async Task AuditSkippedToolCallsAsync(
        AgenticChatSession session,
        IReadOnlyList<AiToolCall> toolCalls,
        ToolExecutionStatus executionStatus,
        string errorCode,
        string errorMessage)
    {
        foreach (var toolCall in toolCalls)
        {
            var tool = FindTool(
                session.Tools,
                toolCall.Name);
            var validation = tool?.Validate(toolCall.Arguments)
                ?? ToolValidationResult.Invalid("unknown_tool", "The requested tool is not available.");
            var policy = toolPolicy.Decide(tool?.Policy, toolCall.Name);

            await auditLogWriter.WriteAsync(
                CreateSkippedResult(
                    toolCall,
                    tool,
                    validation,
                    policy,
                    executionStatus,
                    errorCode,
                    errorMessage),
                CreateContext(session),
                CancellationToken.None);
        }
    }

    private static AgentToolExecutionResult CreateSkippedResult(
        AiToolCall toolCall,
        IAgentTool? tool,
        ToolValidationResult validation,
        ToolPolicyDecision policy,
        ToolExecutionStatus executionStatus,
        string? errorCode,
        string? errorMessage)
    {
        return new AgentToolExecutionResult(
            toolCall.Id,
            toolCall.Name,
            AgentToolSchemaVersion.Resolve(toolCall.SchemaVersion),
            tool is null ? AgentToolSchemaVersion.Resolve(toolCall.SchemaVersion) : tool.Definition.SchemaVersion,
            validation,
            policy,
            ToolApprovalState.NotRequired,
            executionStatus,
            null,
            errorCode,
            errorMessage);
    }

    private static AgentToolExecutionContext CreateContext(AgenticChatSession session)
    {
        return new AgentToolExecutionContext(
            session.ConversationId,
            session.TenantId,
            session.UserId,
            session.Settings.CorrelationId,
            session.Options.PolicyVersion,
            session.ApproveRiskyTools);
    }

    private static IAgentTool? FindTool(
        IReadOnlyList<IAgentTool> tools,
        string toolName)
    {
        return tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Definition.Name, toolName, StringComparison.Ordinal));
    }
}
