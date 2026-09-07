using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Generation.ModelGateway;

namespace GenAIPlatform.Application.Generation.Chat;

public sealed record DirectChatCommand(
    string Message,
    string? Model = null,
    double? Temperature = null,
    int? MaxOutputTokens = null,
    string? CorrelationId = null)
    : IRequest<DirectChatResponse>, IModelChatCommand;
