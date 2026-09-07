using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Domain.Prompts;

namespace GenAIPlatform.Infrastructure.Observability.Logging;

internal sealed record AiRequestLogWriteRequest(
    string CorrelationId,
    PromptMetadata? Prompt,
    string Provider,
    string Model,
    string Status,
    string? ErrorCode,
    TimeSpan Latency,
    AiModelUsage? Usage,
    int? EmbeddingTokens,
    string? EmbeddingProvider,
    string? EmbeddingModel,
    TimeSpan? RetrievalLatency,
    IReadOnlyList<RetrievedDocumentReference> RetrievedDocuments,
    DateTimeOffset CreatedAtUtc);
