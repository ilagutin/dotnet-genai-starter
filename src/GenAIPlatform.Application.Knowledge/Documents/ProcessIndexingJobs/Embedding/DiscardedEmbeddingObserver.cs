using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Embedding;

internal sealed class DiscardedEmbeddingObserver(
    IDiscardedEmbeddingUsageLogger usageLogger,
    TimeProvider timeProvider,
    ILogger<DiscardedEmbeddingObserver> logger)
{
    public async Task CancelAndObserveAsync(
        Document document,
        IndexingJob indexingJob,
        EmbeddingRequest request,
        Task<EmbeddingResponse> embeddingTask,
        CancellationTokenSource providerCancellation,
        long embeddingStarted)
    {
        var cancellationOwnedByObservation = false;

        try
        {
            await providerCancellation.CancelAsync();

            var observationTask = ObserveCompletedAsync(
                document,
                indexingJob,
                request,
                embeddingTask,
                providerCancellation,
                embeddingStarted);
            cancellationOwnedByObservation = true;
            var completed = await Task.WhenAny(
                observationTask,
                Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));

            if (completed != observationTask)
            {
                logger.LogWarning(
                    "Embedding provider call did not observe cancellation promptly; it will be observed when it completes. DocumentId={DocumentId} IndexingJobId={IndexingJobId} RequestedEmbeddingModel={RequestedEmbeddingModel}",
                    document.Id,
                    indexingJob.Id,
                    request.Model);
            }
        }
        finally
        {
            if (!cancellationOwnedByObservation)
            {
                providerCancellation.Dispose();
            }
        }
    }

    public async Task ObserveCompletedAsync(
        Document document,
        IndexingJob indexingJob,
        EmbeddingRequest request,
        Task<EmbeddingResponse> embeddingTask,
        CancellationTokenSource? providerCancellation = null,
        long embeddingStarted = 0)
    {
        try
        {
            var response = await embeddingTask;
            await LogDiscardedEmbeddingUsageAsync(
                document,
                indexingJob,
                request,
                response,
                timeProvider.GetElapsedTime(embeddingStarted));
            logger.LogWarning(
                "Discarded embedding provider response after indexing job ownership was lost or worker cancellation. DocumentId={DocumentId} IndexingJobId={IndexingJobId} EmbeddingProvider={EmbeddingProvider} EmbeddingModel={EmbeddingModel} InputTokens={InputTokens}",
                document.Id,
                indexingJob.Id,
                response.Provider,
                response.Model,
                response.InputTokens);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Embedding provider call completed with an exception after indexing abandoned the result. DocumentId={DocumentId} IndexingJobId={IndexingJobId} RequestedEmbeddingModel={RequestedEmbeddingModel} ExceptionType={ExceptionType}",
                document.Id,
                indexingJob.Id,
                request.Model,
                exception.GetType().Name);
        }
        finally
        {
            providerCancellation?.Dispose();
        }
    }

    private async Task LogDiscardedEmbeddingUsageAsync(
        Document document,
        IndexingJob indexingJob,
        EmbeddingRequest request,
        EmbeddingResponse response,
        TimeSpan latency)
    {
        try
        {
            await usageLogger.LogDiscardedEmbeddingAsync(
                ResolveDiscardedEmbeddingCorrelationId(document, indexingJob, request, response),
                response.Provider,
                response.Model,
                response.InputTokens,
                latency);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to log discarded embedding usage after indexing job ownership was lost or worker cancellation. DocumentId={DocumentId} IndexingJobId={IndexingJobId} EmbeddingProvider={EmbeddingProvider} EmbeddingModel={EmbeddingModel} ExceptionType={ExceptionType}",
                document.Id,
                indexingJob.Id,
                response.Provider,
                response.Model,
                exception.GetType().Name);
        }
    }

    private static string ResolveDiscardedEmbeddingCorrelationId(
        Document document,
        IndexingJob indexingJob,
        EmbeddingRequest request,
        EmbeddingResponse response)
    {
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return request.CorrelationId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(response.CorrelationId))
        {
            return response.CorrelationId.Trim();
        }

        return $"indexing-document-{document.Id:n}-job-{indexingJob.Id:n}";
    }
}
