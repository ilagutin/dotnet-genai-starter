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
                await state.ApplyModelResponseAsync(
                    response,
                    budgetGuard,
                    timeout.Token);

                var proposedToolCalls = response.ProposedToolCalls ?? [];
                if (budgetGuard.IsExceeded(state.TotalTokens, state.EstimatedCost, session.Options))
                {
                    await AuditBudgetSkippedToolsAsync(
                        session,
                        state,
                        proposedToolCalls);
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

    private async Task AuditBudgetSkippedToolsAsync(
        AgenticChatSession session,
        AgenticChatLoopState state,
        IReadOnlyList<AiToolCall> proposedToolCalls)
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
            "budget_exceeded",
            "The configured token or cost budget was exceeded before tool execution.");
    }
}
