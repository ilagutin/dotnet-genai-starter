using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Lease;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Failure;

internal sealed class IndexingJobFailureRecorder(
    IIndexingJobRepository repository,
    IOptions<DocumentIngestionOptions> ingestionOptions,
    IndexingJobLeaseCoordinator leaseCoordinator,
    IndexingJobFailurePolicy failurePolicy,
    ILogger<IndexingJobFailureRecorder> logger)
{
    public async Task<IndexingJobProcessingResult> RecordMissingDocumentAsync(
        IndexingJob indexingJob,
        CancellationToken cancellationToken)
    {
        var failureRecorded = await repository.MarkIndexingFailedAsync(
            indexingJob.DocumentId,
            indexingJob,
            "Document metadata was not found.",
            retry: false,
            TimeSpan.Zero,
            cancellationToken);

        return failureRecorded
            ? IndexingJobProcessingResult.Failed
            : IndexingJobProcessingResult.None;
    }

    public async Task RecordCancellationAsync(
        IndexingJob indexingJob,
        IndexingAttemptState attemptState)
    {
        if (attemptState.AttemptConsumed)
        {
            await RecordInterruptedProcessingAttemptAsync(indexingJob);
            return;
        }

        await leaseCoordinator.ReleaseAfterCancellationAsync(indexingJob);
    }

    public async Task<IndexingFailureRecord> RecordFailureAsync(
        IndexingJob indexingJob,
        Exception exception,
        IndexingAttemptState attemptState,
        CancellationToken cancellationToken)
    {
        var options = ingestionOptions.Value;
        var shouldRetry = failurePolicy.ShouldRetry(
            exception,
            indexingJob.Attempts,
            indexingJob.MaxAttempts);
        var publicFailureReason = failurePolicy.ToPublicFailureReason(exception);

        LogIndexingFailure(
            indexingJob,
            exception,
            shouldRetry,
            publicFailureReason);

        var failureRecorded = await repository.MarkIndexingFailedAsync(
            indexingJob.DocumentId,
            indexingJob,
            publicFailureReason,
            shouldRetry,
            GetRetryDelay(options),
            attemptState.AttemptConsumed ? CancellationToken.None : cancellationToken);

        if (!failureRecorded)
        {
            return IndexingFailureRecord.None;
        }

        return shouldRetry
            ? IndexingFailureRecord.Retried
            : IndexingFailureRecord.Failed;
    }

    public async Task<IndexingFailureRecord> RecordSchemaReadinessFailureAfterSideEffectsAsync(
        IndexingJob indexingJob,
        DocumentIngestionOptions options,
        DocumentIndexingSchemaNotReadyException exception)
    {
        var shouldRetry = indexingJob.Attempts < Math.Max(1, indexingJob.MaxAttempts);
        logger.LogError(
            exception,
            "Document indexing schema became unavailable after indexing side effects started. Recording bounded indexing attempt outcome. DocumentId={DocumentId} IndexingJobId={IndexingJobId} Retry={Retry}",
            indexingJob.DocumentId,
            indexingJob.Id,
            shouldRetry);

        try
        {
            var failureRecorded = await repository.MarkIndexingFailedAsync(
                indexingJob.DocumentId,
                indexingJob,
                "Document indexing schema is not ready.",
                shouldRetry,
                GetRetryDelay(options),
                CancellationToken.None);

            if (!failureRecorded)
            {
                return IndexingFailureRecord.None;
            }

            return shouldRetry
                ? IndexingFailureRecord.Retried
                : IndexingFailureRecord.Failed;
        }
        catch (Exception recordException)
        {
            logger.LogWarning(
                recordException,
                "Failed to record indexing schema readiness failure after side effects started. DocumentId={DocumentId} IndexingJobId={IndexingJobId} ExceptionType={ExceptionType}",
                indexingJob.DocumentId,
                indexingJob.Id,
                recordException.GetType().Name);

            return IndexingFailureRecord.None;
        }
    }

    private async Task RecordInterruptedProcessingAttemptAsync(IndexingJob indexingJob)
    {
        try
        {
            var options = ingestionOptions.Value;
            var shouldRetry = indexingJob.Attempts < Math.Max(1, indexingJob.MaxAttempts);
            var failureRecorded = await repository.MarkIndexingFailedAsync(
                indexingJob.DocumentId,
                indexingJob,
                "Indexing job was interrupted while processing the document.",
                shouldRetry,
                GetRetryDelay(options),
                CancellationToken.None);

            if (failureRecorded)
            {
                logger.LogInformation(
                    "Recorded interrupted indexing attempt after processing side effects started. DocumentId={DocumentId} IndexingJobId={IndexingJobId} Retry={Retry}",
                    indexingJob.DocumentId,
                    indexingJob.Id,
                    shouldRetry);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to record interrupted indexing attempt after cancellation. DocumentId={DocumentId} IndexingJobId={IndexingJobId} ExceptionType={ExceptionType}",
                indexingJob.DocumentId,
                indexingJob.Id,
                exception.GetType().Name);
        }
    }

    private void LogIndexingFailure(
        IndexingJob indexingJob,
        Exception exception,
        bool retry,
        string publicFailureReason)
    {
        logger.LogWarning(
            "Document indexing failed. DocumentId={DocumentId} IndexingJobId={IndexingJobId} Retry={Retry} ErrorCode={ErrorCode} FailureReason={FailureReason} ExceptionType={ExceptionType}",
            indexingJob.DocumentId,
            indexingJob.Id,
            retry,
            failurePolicy.ToPublicErrorCode(exception),
            publicFailureReason,
            exception.GetType().Name);
    }

    private static TimeSpan GetRetryDelay(DocumentIngestionOptions options)
    {
        return TimeSpan.FromSeconds(Math.Max(0, options.IndexingRetryDelaySeconds));
    }
}
