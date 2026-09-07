using GenAIPlatform.Application.Core.ModelClients;

namespace GenAIPlatform.Application.Agentic.Chat;

public sealed record AgenticChatResponse(
    Guid ConversationId,
    string Status,
    string Answer,
    string Model,
    string? Provider,
    string CorrelationId,
    int Steps,
    int ToolCalls,
    int TotalTokens,
    decimal EstimatedCost,
    IReadOnlyList<AgentToolCallResult> ToolResults,
    AiModelUsage? Usage);
