using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Generation.ModelGateway;

namespace GenAIPlatform.Application.Agentic.Chat;

public sealed record AgenticChatCommand(
    string? Message = null,
    string? Model = null,
    double? Temperature = null,
    int? MaxOutputTokens = null,
    string? CorrelationId = null,
    bool ApproveRiskyTools = false)
    : IRequest<AgenticChatResponse>, IModelChatCommand;
