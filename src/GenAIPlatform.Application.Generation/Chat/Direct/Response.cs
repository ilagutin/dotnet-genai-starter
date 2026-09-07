using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Prompts;

namespace GenAIPlatform.Application.Generation.Chat;

public sealed record DirectChatResponse(
    string Message,
    string Model,
    string Provider,
    AiModelUsage? Usage,
    PromptMetadata Prompt,
    string CorrelationId);
