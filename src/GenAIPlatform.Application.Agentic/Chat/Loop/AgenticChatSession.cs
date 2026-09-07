using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Generation.ModelGateway;

namespace GenAIPlatform.Application.Agentic.Chat;

internal sealed record AgenticChatSession(
    Guid ConversationId,
    string TenantId,
    string UserId,
    ModelGatewayRequestSettings Settings,
    AgenticChatOptions Options,
    IReadOnlyList<IAgentTool> Tools,
    AgenticPromptMessages Prompt,
    bool ApproveRiskyTools);
