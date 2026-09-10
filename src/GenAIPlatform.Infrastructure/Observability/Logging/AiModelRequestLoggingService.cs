using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Domain.Observability;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.Infrastructure.Observability.Logging;

public sealed class AiModelRequestLoggingService(
    AiRequestLogWriter logWriter,
    TimeProvider timeProvider,
    ILogger<AiModelRequestLoggingService> logger)
{
    private const string NoModelProvider = "no-model";
    private const string DiscardedEmbeddingErrorCode = "indexing_abandoned";

    public async Task<AiModelResponse> CompleteAndLogAsync(
        IAiModelClient modelClient,
        AiModelRequest request,
        TimeSpan? retrievalLatency,
        int? embeddingTokens,
        string? embeddingProvider,
        string? embeddingModel,
        IReadOnlyList<RetrievedDocumentReference> retrievedDocuments,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var createdAtUtc = timeProvider.GetUtcNow();

        AiModelResponse response;
        try
        {
            response = await modelClient.CompleteAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            var latency = timeProvider.GetElapsedTime(started);
            try
            {
                await logWriter.WriteAsync(
                    new AiRequestLogWriteRequest(
                        request.CorrelationId,
                        request.Prompt,
                        NormalizeProvider(exception),
                        request.Model,
                        AiRequestLogStatus.Failed.ToPublicValue(),
                        NormalizeErrorCode(exception),
                        latency,
                        Usage: null,
                        embeddingTokens,
                        embeddingProvider,
                        embeddingModel,
                        retrievalLatency,
                        retrievedDocuments,
                        createdAtUtc),
                    CancellationToken.None);
            }
            catch (Exception loggingException)
            {
                logger.LogError(
                    loggingException,
                    "AI request failure logging failed for correlation id {CorrelationId}. Rethrowing original model client exception.",
                    request.CorrelationId);
            }

            throw;
        }

        var succeededLatency = timeProvider.GetElapsedTime(started);
        await logWriter.WriteAsync(
            new AiRequestLogWriteRequest(
                request.CorrelationId,
                request.Prompt,
                response.Provider,
                response.Model,
                AiRequestLogStatus.Succeeded.ToPublicValue(),
                ErrorCode: null,
                succeededLatency,
                response.Usage,
                embeddingTokens,
                embeddingProvider,
                embeddingModel,
                retrievalLatency,
                retrievedDocuments,
                createdAtUtc),
            CancellationToken.None);

        return response;
    }

    public async Task LogSucceededWithoutModelAsync(
        string correlationId,
        string model,
        TimeSpan latency,
        int? embeddingTokens,
        string? embeddingProvider,
        string? embeddingModel,
        TimeSpan? retrievalLatency,
        IReadOnlyList<RetrievedDocumentReference> retrievedDocuments)
    {
        await logWriter.WriteAsync(
            new AiRequestLogWriteRequest(
                correlationId,
                Prompt: null,
                NoModelProvider,
                model,
                AiRequestLogStatus.Succeeded.ToPublicValue(),
                ErrorCode: null,
                latency,
                Usage: null,
                embeddingTokens,
                embeddingProvider,
                embeddingModel,
                retrievalLatency,
                retrievedDocuments,
                timeProvider.GetUtcNow()),
            CancellationToken.None);
    }

    public async Task LogDiscardedEmbeddingAsync(
        string correlationId,
        string provider,
        string model,
        int? embeddingTokens,
        TimeSpan latency)
    {
        await logWriter.WriteAsync(
            new AiRequestLogWriteRequest(
                correlationId,
                Prompt: null,
                provider,
                model,
                AiRequestLogStatus.Succeeded.ToPublicValue(),
                DiscardedEmbeddingErrorCode,
                latency,
                Usage: null,
                embeddingTokens,
                provider,
                model,
                RetrievalLatency: null,
                RetrievedDocuments: [],
                timeProvider.GetUtcNow()),
            CancellationToken.None);
    }

    private static string NormalizeProvider(Exception exception)
    {
        return exception is AiModelException modelException &&
               !string.IsNullOrWhiteSpace(modelException.Provider)
            ? modelException.Provider
            : "unknown";
    }

    private static string NormalizeErrorCode(Exception exception)
    {
        if (exception is AiModelException modelException &&
            !string.IsNullOrWhiteSpace(modelException.ErrorCode))
        {
            return modelException.ErrorCode;
        }

        return exception switch
        {
            OperationCanceledException => "request_canceled",
            TimeoutException => "timeout",
            _ => "provider_error"
        };
    }
}
