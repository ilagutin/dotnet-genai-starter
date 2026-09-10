using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Failure;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Lease;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;

internal sealed class IndexingJobBatchProcessor(
    IIndexingJobRepository repository,
    IOptions<DocumentIngestionOptions> ingestionOptions,
    IndexingJobLeaseCoordinator leaseCoordinator,
    IndexingJobProcessor jobProcessor,
    IndexingJobFailureRecorder failureRecorder,
    ILogger<IndexingJobBatchProcessor> logger)
{
    public async Task<ProcessIndexingJobsResponse> ProcessAsync(
        ProcessIndexingJobsCommand request,
        CancellationToken cancellationToken)
    {
        var options = ingestionOptions.Value;
        var maxJobs = IndexingJobLimits.ResolveMaxJobs(
            request.MaxJobs,
            options.MaxIndexingJobsPerPoll);
        var expiredOrExhaustedFailed = await repository.MarkExpiredIndexingJobsFailedAsync(
            leaseCoordinator.GetProcessingLeaseDuration(options),
            cancellationToken);
        var counters = new IndexingJobCounters(expiredOrExhaustedFailed);

        for (var i = 0; i < maxJobs; i++)
        {
            var indexingJob = await repository.ClaimNextPendingJobAsync(
                request.WorkerId,
                leaseCoordinator.GetProcessingLeaseDuration(options),
                cancellationToken);

            if (indexingJob is null)
            {
                break;
            }

            counters.MarkClaimed();
            await ProcessClaimedJobAsync(
                indexingJob,
                request.CorrelationId,
                options,
                counters,
                cancellationToken);
        }

        return counters.ToResponse();
    }

    private async Task ProcessClaimedJobAsync(
        IndexingJob indexingJob,
        string? correlationId,
        DocumentIngestionOptions options,
        IndexingJobCounters counters,
        CancellationToken cancellationToken)
    {
        var attemptState = new IndexingAttemptState();
        try
        {
            var result = await jobProcessor.ProcessAsync(
                indexingJob,
                correlationId,
                attemptState,
                cancellationToken);

            counters.Mark(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await failureRecorder.RecordCancellationAsync(
                indexingJob,
                attemptState);
            throw;
        }
        catch (DocumentIndexingCompletionUnknownException exception)
        {
            logger.LogWarning(
                exception,
                "Preserved indexing job state after completion failure because the durable completion outcome is unknown. DocumentId={DocumentId} IndexingJobId={IndexingJobId}",
                exception.DocumentId,
                exception.IndexingJobId);
        }
        catch (DocumentIndexingSchemaNotReadyException exception)
        {
            await RecordSchemaReadinessFailureAsync(
                indexingJob,
                options,
                counters,
                attemptState,
                exception);
        }
        catch (StaleIndexingJobException)
        {
            logger.LogInformation(
                "Skipped stale indexing job after lease ownership changed. DocumentId={DocumentId} IndexingJobId={IndexingJobId}",
                indexingJob.DocumentId,
                indexingJob.Id);
        }
        catch (Exception exception)
        {
            var failureRecord = await failureRecorder.RecordFailureAsync(
                indexingJob,
                exception,
                attemptState,
                cancellationToken);

            counters.Mark(failureRecord);
        }
    }

    private async Task RecordSchemaReadinessFailureAsync(
        IndexingJob indexingJob,
        DocumentIngestionOptions options,
        IndexingJobCounters counters,
        IndexingAttemptState attemptState,
        DocumentIndexingSchemaNotReadyException exception)
    {
        if (!attemptState.AttemptConsumed)
        {
            logger.LogError(
                exception,
                "Document indexing schema is not ready before indexing side effects started. Returning the claimed job to the queue without recording document failure. DocumentId={DocumentId} IndexingJobId={IndexingJobId}",
                indexingJob.DocumentId,
                indexingJob.Id);

            await leaseCoordinator.ReleaseAfterSchemaReadinessFailureAsync(indexingJob);
            return;
        }

        var failureRecord = await failureRecorder.RecordSchemaReadinessFailureAfterSideEffectsAsync(
            indexingJob,
            options,
            exception);

        counters.Mark(failureRecord);
    }
}
