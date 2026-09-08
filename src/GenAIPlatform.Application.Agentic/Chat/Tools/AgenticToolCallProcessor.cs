using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Chat;

internal sealed class AgenticToolCallProcessor(
    AgentToolExecutor toolExecutor,
    AgentToolAuditWriter auditWriter)
{
    public async Task<AgenticToolProcessingOutcome> ProcessAsync(
        AgenticChatSession session,
        AgenticChatLoopState state,
        IReadOnlyList<AiToolCall> proposedToolCalls,
        int step,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < proposedToolCalls.Count; index++)
        {
            var toolCall = proposedToolCalls[index];
            state.AddToolCallCount(1);

            if (state.ToolCallCount > session.Options.MaxToolCalls)
            {
                await AuditToolLimitSkippedCallsAsync(
                    session,
                    state,
                    proposedToolCalls,
                    index);
                return new AgenticToolProcessingOutcome(
                    AgenticChatStatus.ToolLimitExceeded,
                    "Agent loop stopped after reaching the configured tool-call limit.",
                    step);
            }

            var result = await toolExecutor.ExecuteAsync(
                session,
                toolCall,
                cancellationToken);

            if (IsRejectedOrFailed(result.ExecutionStatus))
            {
                state.ToolResults.Add(result);
                await AuditRemainingAfterTerminalResultAsync(
                    session,
                    state,
                    proposedToolCalls,
                    index,
                    result.ExecutionStatus == ToolExecutionStatus.Failed ? "prior_tool_failed" : "prior_tool_rejected",
                    result.ExecutionStatus == ToolExecutionStatus.Failed
                        ? "A previous tool call did not confirm success before these proposed calls could execute."
                        : "A previous tool call was rejected before these proposed calls could execute.");

                return result.ExecutionStatus == ToolExecutionStatus.Failed
                    ? new AgenticToolProcessingOutcome(
                        AgenticChatStatus.ToolFailed,
                        $"Tool call {toolCall.Name} did not confirm successful completion.",
                        step)
                    : new AgenticToolProcessingOutcome(
                        AgenticChatStatus.ToolRejected,
                        $"Tool call {toolCall.Name} was rejected by backend policy.",
                        step);
            }

            if (result.ExecutionStatus == ToolExecutionStatus.ApprovalRequired)
            {
                state.ToolResults.Add(result);
                await AuditRemainingAfterTerminalResultAsync(
                    session,
                    state,
                    proposedToolCalls,
                    index,
                    "approval_required",
                    "A previous tool call required approval before these proposed calls could execute.");
                return new AgenticToolProcessingOutcome(
                    AgenticChatStatus.ApprovalRequired,
                    $"Tool call {toolCall.Name} requires simulated approval.",
                    step);
            }

            state.AddToolResult(
                toolCall,
                result);
        }

        return AgenticToolProcessingOutcome.Continue(step);
    }

    private async Task AuditToolLimitSkippedCallsAsync(
        AgenticChatSession session,
        AgenticChatLoopState state,
        IReadOnlyList<AiToolCall> proposedToolCalls,
        int startIndex)
    {
        var skippedCalls = proposedToolCalls
            .Skip(startIndex)
            .ToArray();
        state.AddToolCallCount(skippedCalls.Length - 1);
        await auditWriter.AuditSkippedToolCallsAsync(
            session,
            skippedCalls,
            ToolExecutionStatus.Rejected,
            "tool_limit_exceeded",
            "The configured tool-call limit was exceeded.");
    }

    private async Task AuditRemainingAfterTerminalResultAsync(
        AgenticChatSession session,
        AgenticChatLoopState state,
        IReadOnlyList<AiToolCall> proposedToolCalls,
        int completedIndex,
        string errorCode,
        string errorMessage)
    {
        var remainingCalls = proposedToolCalls
            .Skip(completedIndex + 1)
            .ToArray();
        if (remainingCalls.Length == 0)
        {
            return;
        }

        state.AddToolCallCount(remainingCalls.Length);
        await auditWriter.AuditSkippedToolCallsAsync(
            session,
            remainingCalls,
            ToolExecutionStatus.NotExecuted,
            errorCode,
            errorMessage);
    }

    private static bool IsRejectedOrFailed(ToolExecutionStatus status)
    {
        return status is
            ToolExecutionStatus.Rejected or
            ToolExecutionStatus.ValidationFailed or
            ToolExecutionStatus.Failed;
    }
}
