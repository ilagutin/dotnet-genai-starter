using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Chat;

internal sealed class AgenticChatLoopState
{
    private readonly AgenticChatSession session;
    private readonly List<AiChatMessage> messages;
    private readonly IReadOnlyList<AiChatMessage> readOnlyMessages;
    private AiModelUsage? usage;

    public AgenticChatLoopState(AgenticChatSession session)
    {
        this.session = session;
        messages = session.Prompt.Messages.ToList();
        readOnlyMessages = messages.AsReadOnly();
    }

    public IReadOnlyList<AiChatMessage> Messages => readOnlyMessages;

    public List<AgentToolCallResult> ToolResults { get; } = [];

    public string? Provider { get; private set; }

    public string LastContent { get; private set; } = string.Empty;

    public int ToolCallCount { get; private set; }

    public int TotalTokens { get; private set; }

    public decimal EstimatedCost { get; private set; }

    public async Task ApplyModelResponseAsync(
        AiModelResponse response,
        AgenticBudgetGuard budgetGuard,
        CancellationToken cancellationToken)
    {
        Provider = response.Provider;
        usage = MergeUsage(usage, response.Usage);
        TotalTokens += response.Usage?.TotalTokens ?? 0;
        EstimatedCost += await budgetGuard.EstimateResponseCostAsync(
            response,
            session.Options,
            cancellationToken);
        LastContent = response.Content;

        var proposedToolCalls = response.ProposedToolCalls ?? [];
        if (proposedToolCalls.Count > 0)
        {
            messages.Add(new AiChatMessage(
                AiMessageRole.Assistant,
                response.Content,
                ToolCalls: proposedToolCalls));
        }
    }

    public void AddToolCallCount(int count)
    {
        ToolCallCount += count;
    }

    public void AddToolResult(
        AiToolCall toolCall,
        AgentToolCallResult result)
    {
        ToolResults.Add(result);
        messages.Add(new AiChatMessage(
            AiMessageRole.Tool,
            result.Result ?? string.Empty,
            ToolCallId: toolCall.Id));
    }

    public AgenticChatResponse CreateResponse(
        AgenticChatStatus status,
        string answer,
        int steps)
    {
        return new AgenticChatResponse(
            session.ConversationId,
            status.ToPublicValue(),
            answer,
            session.Settings.Model,
            Provider,
            session.Settings.CorrelationId,
            steps,
            ToolCallCount,
            TotalTokens,
            EstimatedCost,
            ToolResults,
            usage);
    }

    private static AiModelUsage? MergeUsage(
        AiModelUsage? current,
        AiModelUsage? next)
    {
        if (current is null)
        {
            return next;
        }

        if (next is null)
        {
            return current;
        }

        return new AiModelUsage(
            Add(current.InputTokens, next.InputTokens),
            Add(current.OutputTokens, next.OutputTokens),
            Add(current.TotalTokens, next.TotalTokens));
    }

    private static int? Add(int? left, int? right)
    {
        return left is null && right is null ? null : (left ?? 0) + (right ?? 0);
    }
}
