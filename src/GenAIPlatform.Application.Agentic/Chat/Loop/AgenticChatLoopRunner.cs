using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Chat;

internal sealed class AgenticChatLoopRunner(
    IAiModelClient modelClient,
    IAiModelRequestLogger requestLoggingService,
    AgenticBudgetGuard budgetGuard,
    AgenticToolCallProcessor toolCallProcessor,
    AgentToolAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<AgenticChatResponse> RunAsync(
        AgenticChatSession session,
        CancellationToken cancellationToken)
    {
        var state = new AgenticChatLoopState(session);
        var startedAt = timeProvider.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, session.Options.TimeoutSeconds)));

        try
        {
            for (var step = 1; step <= Math.Max(1, session.Options.MaxSteps); step++)
            {
                if (timeProvider.GetElapsedTime(startedAt) > TimeSpan.FromSeconds(session.Options.TimeoutSeconds))
                {
                    return state.CreateResponse(
                        AgenticChatStatus.TimedOut,
                        "Agent loop timed out before completing.",
                        step - 1);
                }

                var response = await CompleteStepAsync(
                    session,
                    state.Messages,
                    timeout.Token);
                var usageFailure = await state.ApplyModelResponseAsync(
                    response,
                    budgetGuard,
                    timeout.Token);

                var proposedToolCalls = response.ProposedToolCalls ?? [];
                if (usageFailure is { } usageStatus)
                {
                    var code = usageStatus == AgenticChatStatus.UsageUnavailable ? "usage_unavailable" : "invalid_usage";
                    const string reason = "Agent loop stopped because usable token usage was not available for the current response.";
                    await AuditSkippedToolsAsync(session, state, proposedToolCalls, code, reason, preserveStopReason: true);
                    return state.CreateResponse(usageStatus, reason, step);
                }

                if (budgetGuard.IsExceeded(state.TotalTokens, state.EstimatedCost, session.Options))
                {
                    await AuditSkippedToolsAsync(
                        session,
                        state,
                        proposedToolCalls,
                        "budget_exceeded",
                        "The configured token or cost budget was reached before tool execution.");
                    return state.CreateResponse(
                        AgenticChatStatus.BudgetExceeded,
                        "Agent loop stopped after reaching the configured token or cost budget.",
                        step);
                }

                if (proposedToolCalls.Count == 0)
                {
                    return state.CreateResponse(
                        AgenticChatStatus.Succeeded,
                        string.IsNullOrWhiteSpace(response.Content) ? "Agent loop completed." : response.Content,
                        step);
                }

                var toolOutcome = await toolCallProcessor.ProcessAsync(
                    session,
                    state,
                    proposedToolCalls,
                    step,
                    timeout.Token);

                if (toolOutcome.IsTerminal)
                {
                    return state.CreateResponse(
                        toolOutcome.Status,
                        toolOutcome.Answer,
                        toolOutcome.Step);
                }
            }

            return state.CreateResponse(
                AgenticChatStatus.StepLimitExceeded,
                string.IsNullOrWhiteSpace(state.LastContent)
                    ? "Agent loop stopped after reaching the configured step limit."
                    : state.LastContent,
                Math.Max(1, session.Options.MaxSteps));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return state.CreateResponse(
                AgenticChatStatus.TimedOut,
                "Agent loop timed out before completing.",
                Math.Max(0, state.ToolResults.Count));
        }
    }

    private async Task<AiModelResponse> CompleteStepAsync(
        AgenticChatSession session,
        IReadOnlyList<AiChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var aiRequest = new AiModelRequest(
            session.Settings.CorrelationId,
            session.Settings.Model,
            messages,
            session.Settings.Temperature,
            session.Settings.MaxOutputTokens,
            session.Prompt.Prompt,
            Tools: session.Tools.Select(static tool => tool.Definition).ToArray());

        return await requestLoggingService.CompleteAndLogAsync(
            modelClient,
            aiRequest,
            retrievalLatency: null,
            embeddingTokens: null,
            embeddingProvider: null,
            embeddingModel: null,
            retrievedDocuments: [],
            cancellationToken);
    }

    private async Task AuditSkippedToolsAsync(
        AgenticChatSession session,
        AgenticChatLoopState state,
        IReadOnlyList<AiToolCall> proposedToolCalls,
        string errorCode,
        string reason,
        bool preserveStopReason = false)
    {
        if (proposedToolCalls.Count == 0)
        {
            return;
        }

        state.AddToolCallCount(proposedToolCalls.Count);
        await auditWriter.AuditSkippedToolCallsAsync(
            session,
            proposedToolCalls,
            ToolExecutionStatus.NotExecuted,
            errorCode,
            reason,
            preserveStopReason);
    }
}
