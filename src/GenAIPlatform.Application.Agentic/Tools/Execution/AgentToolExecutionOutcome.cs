using System.Text.Json;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Tools.Execution;

internal sealed record AgentToolExecutionOutcome(
    ToolApprovalState ApprovalState,
    ToolExecutionStatus ExecutionStatus,
    JsonElement? Output,
    ToolExecutionPayloadMetadata? PayloadMetadata,
    string? ErrorCode,
    string? ErrorMessage,
    Exception? Exception = null)
{
    public static AgentToolExecutionOutcome Rejected(
        string errorCode,
        string errorMessage)
    {
        return new AgentToolExecutionOutcome(
            ToolApprovalState.NotRequired,
            ToolExecutionStatus.Rejected,
            null,
            null,
            errorCode,
            errorMessage);
    }

    public static AgentToolExecutionOutcome ValidationFailed(
        string? errorCode,
        string? errorMessage)
    {
        return new AgentToolExecutionOutcome(
            ToolApprovalState.NotRequired,
            ToolExecutionStatus.ValidationFailed,
            null,
            null,
            errorCode,
            errorMessage);
    }

    public static AgentToolExecutionOutcome ApprovalRequired(string reason)
    {
        return new AgentToolExecutionOutcome(
            ToolApprovalState.Required,
            ToolExecutionStatus.ApprovalRequired,
            null,
            null,
            "approval_required",
            reason);
    }

    public static AgentToolExecutionOutcome Executed(
        bool approvalWasRequired,
        ToolExecutionStatus status,
        JsonElement output,
        ToolExecutionPayloadMetadata? payloadMetadata,
        string? errorCode,
        string? errorMessage)
    {
        return new AgentToolExecutionOutcome(
            approvalWasRequired ? ToolApprovalState.SimulatedApproved : ToolApprovalState.NotRequired,
            status,
            output,
            payloadMetadata,
            errorCode,
            errorMessage);
    }

    public static AgentToolExecutionOutcome UnexpectedBackendStatus(
        bool approvalWasRequired,
        ToolExecutionStatus status)
    {
        return new AgentToolExecutionOutcome(
            approvalWasRequired ? ToolApprovalState.SimulatedApproved : ToolApprovalState.NotRequired,
            ToolExecutionStatus.Failed,
            null,
            null,
            "tool_unexpected_execution_status",
            $"Tool returned unexpected backend execution status '{status}'.");
    }

    public static AgentToolExecutionOutcome Failed(
        string errorCode,
        string errorMessage,
        Exception? exception = null)
    {
        return new AgentToolExecutionOutcome(
            ToolApprovalState.NotRequired,
            ToolExecutionStatus.Failed,
            null,
            null,
            errorCode,
            errorMessage,
            exception);
    }
}
