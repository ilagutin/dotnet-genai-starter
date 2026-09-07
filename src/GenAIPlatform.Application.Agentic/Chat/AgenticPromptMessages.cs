using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Prompts;

namespace GenAIPlatform.Application.Agentic.Chat;

public sealed record AgenticPromptMessages(
    IReadOnlyList<AiChatMessage> Messages,
    PromptMetadata Prompt);
