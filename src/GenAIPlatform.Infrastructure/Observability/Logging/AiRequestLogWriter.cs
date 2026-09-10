using GenAIPlatform.Application.Core.Configuration;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Infrastructure.Observability.Pricing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Observability.Logging;

public sealed class AiRequestLogWriter(
    IAiRequestLogRepository requestLogRepository,
    AiCostEstimator costEstimator,
    IUserContext userContext,
    IOptions<ApplicationOptions> applicationOptions,
    IOptions<AiRequestLoggingOptions> loggingOptions,
    ILogger<AiRequestLogWriter> logger)
{
    internal async Task WriteAsync(
        AiRequestLogWriteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var cost = await costEstimator.EstimateAsync(
                request.Provider,
                request.Model,
                request.Usage,
                request.EmbeddingTokens,
                request.EmbeddingProvider,
                request.EmbeddingModel,
                request.CreatedAtUtc,
                cancellationToken);

            var entry = new AiRequestLogEntry(
                Guid.NewGuid(),
                ApiVersion: applicationOptions.Value.ApiVersion,
                userContext.IsAuthenticated ? userContext.UserId : null,
                userContext.IsAuthenticated ? userContext.TenantId : null,
                request.CorrelationId,
                request.Provider,
                request.Model,
                request.Status,
                request.ErrorCode,
                request.Latency,
                request.Usage?.InputTokens,
                request.Usage?.OutputTokens,
                request.Usage?.TotalTokens,
                request.EmbeddingTokens,
                cost?.Amount,
                cost?.Currency,
                request.Prompt,
                request.RetrievalLatency,
                request.RetrievedDocuments,
                request.CreatedAtUtc);

            await requestLogRepository.AddAsync(entry, cancellationToken);
        }
        catch (Exception exception)
        {
            if (loggingOptions.Value.FailureMode == AiRequestLoggingFailureMode.FailClosed)
            {
                logger.LogError(
                    exception,
                    "AI request logging failed for correlation id {CorrelationId}. Failing request because configured failure mode is fail-closed.",
                    request.CorrelationId);
                throw new AiRequestLoggingException("AI request logging failed.", exception);
            }

            logger.LogError(
                exception,
                "AI request logging failed for correlation id {CorrelationId}. Continuing because configured failure mode is fail-open.",
                request.CorrelationId);
        }
    }
}
