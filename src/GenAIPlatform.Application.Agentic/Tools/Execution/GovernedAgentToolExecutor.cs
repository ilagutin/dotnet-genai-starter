using System.Text.Json;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Tools.Execution;

internal sealed class GovernedAgentToolExecutor(
    ToolPolicy toolPolicy,
    AgentToolArgumentValidator argumentValidator,
    AgentToolAuditLogWriter auditLogWriter)
{
    public async Task<AgentToolExecutionResult> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var tool = request.Tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Definition.Name, request.ToolName, StringComparison.Ordinal));
        var validation = tool is not null
            ? argumentValidator.Validate(tool, request.Arguments)
            : ToolValidationResult.Invalid("unknown_tool", "The requested tool is not available.");
        var policy = toolPolicy.Decide(tool?.Policy, request.ToolName);
        var outcome = await ExecuteCoreAsync(
            request,
            tool,
            validation,
            policy,
            cancellationToken);
        var result = new AgentToolExecutionResult(
            request.ToolCallId,
            request.ToolName,
            AgentToolSchemaVersion.Resolve(request.RequestedSchemaVersion),
            ResolveAuditSchemaVersion(tool, request.RequestedSchemaVersion),
            validation,
            policy,
            outcome.ApprovalState,
            outcome.ExecutionStatus,
            outcome.Output,
            outcome.ErrorCode,
            outcome.ErrorMessage,
            outcome.Exception);

        await auditLogWriter.WriteAsync(
            result,
            request.Context,
            CancellationToken.None);

        return result;
    }

    private async Task<AgentToolExecutionOutcome> ExecuteCoreAsync(
        AgentToolExecutionRequest request,
        IAgentTool? tool,
        ToolValidationResult validation,
        ToolPolicyDecision policy,
        CancellationToken cancellationToken)
    {
        if (policy.Risk == ToolRisk.Forbidden)
        {
            return AgentToolExecutionOutcome.Rejected("tool_forbidden", policy.Reason);
        }

        if (!validation.IsValid)
        {
            return AgentToolExecutionOutcome.ValidationFailed(
                validation.ErrorCode,
                validation.ErrorMessage);
        }

        if (policy.RequiresApproval && !request.Context.ApproveRiskyTools)
        {
            return AgentToolExecutionOutcome.ApprovalRequired(policy.Reason);
        }

        if (!policy.MayExecute)
        {
            return AgentToolExecutionOutcome.Rejected("tool_not_executable", policy.Reason);
        }

        return tool is null
            ? AgentToolExecutionOutcome.Rejected("unknown_tool", "The requested tool is not available.")
            : await ExecuteBackendToolAsync(
                tool,
                validation.SanitizedArguments,
                policy,
                cancellationToken);
    }

    private static async Task<AgentToolExecutionOutcome> ExecuteBackendToolAsync(
        IAgentTool tool,
        JsonElement sanitizedArguments,
        ToolPolicyDecision policy,
        CancellationToken cancellationToken)
    {
        try
        {
            var execution = await tool.ExecuteAsync(
                sanitizedArguments,
                cancellationToken);
            if (!IsBackendExecutionStatus(execution.Status))
            {
                return AgentToolExecutionOutcome.UnexpectedBackendStatus(
                    policy.RequiresApproval,
                    execution.Status);
            }

            return AgentToolExecutionOutcome.Executed(
                policy.RequiresApproval,
                execution.Status,
                execution.Output,
                execution.ErrorCode,
                execution.ErrorMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AgentToolExecutionOutcome.Failed(
                "tool_execution_canceled",
                "Tool execution was canceled.");
        }
        catch (Exception exception)
        {
            return AgentToolExecutionOutcome.Failed(
                "tool_execution_failed",
                "Tool execution failed.",
                exception);
        }
    }

    private static string ResolveAuditSchemaVersion(
        IAgentTool? tool,
        string? requestedSchemaVersion)
    {
        return tool is null
            ? AgentToolSchemaVersion.Resolve(requestedSchemaVersion)
            : tool.Definition.SchemaVersion;
    }

    private static bool IsBackendExecutionStatus(ToolExecutionStatus status)
    {
        return status is ToolExecutionStatus.Succeeded or ToolExecutionStatus.Failed;
    }
}
