using GenAIPlatform.Application.Core.ModelClients;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.Application.Agentic.Chat;

internal sealed partial class AgenticBudgetGuard(
    IAgenticCostEstimator costEstimator,
    TimeProvider timeProvider,
    ILogger<AgenticBudgetGuard> logger)
{
    public bool IsExceeded(
        int totalTokens,
        decimal estimatedCost,
        AgenticChatOptions options)
    {
        return totalTokens > options.MaxTotalTokens ||
               estimatedCost > options.MaxEstimatedCost;
    }

    public async Task<decimal> EstimateResponseCostAsync(
        AiModelResponse response,
        AgenticChatOptions options,
        AgenticBudgetFallbackState fallbackState,
        CancellationToken cancellationToken)
    {
        try
        {
            var estimate = await costEstimator.EstimateAsync(
                response,
                timeProvider.GetUtcNow(),
                cancellationToken);

            if (estimate is not null)
            {
                return estimate.Value;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogFallback(fallbackState, "estimator_failed", exception.GetType().Name);
            return EstimateFallbackCost(response.Usage?.TotalTokens, options);
        }

        LogFallback(fallbackState, "pricing_unavailable", null);
        return EstimateFallbackCost(response.Usage?.TotalTokens, options);
    }

    private void LogFallback(AgenticBudgetFallbackState state, string reason, string? exceptionType)
    {
        if (state.TryMark())
        {
            LogFallbackUsed(logger, state.ConversationId, state.CorrelationId, reason, exceptionType);
        }
    }

    [LoggerMessage(
        EventId = 4002,
        EventName = "AgenticBudgetFallbackUsed",
        Level = LogLevel.Warning,
        Message = "Agentic budget fallback used for conversation {ConversationId}, correlation {CorrelationId}: {Reason} ({ExceptionType})")]
    private static partial void LogFallbackUsed(
        ILogger logger,
        Guid conversationId,
        string correlationId,
        string reason,
        string? exceptionType);

    private static decimal EstimateFallbackCost(
        int? totalTokens,
        AgenticChatOptions options)
    {
        return Math.Round(
            (totalTokens ?? 0) / 1000m * options.EstimatedCostPerThousandTokens,
            8,
            MidpointRounding.AwayFromZero);
    }
}
