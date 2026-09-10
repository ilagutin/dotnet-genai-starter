using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Lease;

internal sealed class IndexingJobLeaseCoordinator(
    IIndexingJobRepository repository,
    IOptions<DocumentIngestionOptions> ingestionOptions,
    ILogger<IndexingJobLeaseCoordinator> logger)
{
    public TimeSpan GetProcessingLeaseDuration(DocumentIngestionOptions options)
    {
        return TimeSpan.FromSeconds(Math.Max(1, options.ProcessingJobLeaseSeconds));
    }

    public TimeSpan GetLeaseRenewalInterval()
    {
        var leaseSeconds = Math.Max(1, ingestionOptions.Value.ProcessingJobLeaseSeconds);
        var intervalSeconds = Math.Clamp(leaseSeconds / 3, 1, 30);
        return TimeSpan.FromSeconds(intervalSeconds);
    }

    public async Task RenewOrThrowAsync(
        Document document,
        IndexingJob indexingJob,
        CancellationToken cancellationToken)
    {
        var renewed = await repository.RenewProcessingLeaseAsync(
            document.Id,
            indexingJob,
            cancellationToken);

        if (!renewed)
        {
            throw new StaleIndexingJobException();
        }
    }

    public async Task ReleaseAfterCancellationAsync(IndexingJob indexingJob)
    {
        try
        {
            var released = await repository.ReleaseProcessingJobAndRefundAttemptAsync(
                indexingJob.DocumentId,
                indexingJob,
                CancellationToken.None);

            if (released)
            {
                logger.LogInformation(
                    "Released indexing job after cancellation. DocumentId={DocumentId} IndexingJobId={IndexingJobId}",
                    indexingJob.DocumentId,
                    indexingJob.Id);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to release indexing job after cancellation. DocumentId={DocumentId} IndexingJobId={IndexingJobId} ExceptionType={ExceptionType}",
                indexingJob.DocumentId,
                indexingJob.Id,
                exception.GetType().Name);
        }
    }

    public async Task ReleaseAfterSchemaReadinessFailureAsync(IndexingJob indexingJob)
    {
        try
        {
            var released = await repository.ReleaseProcessingJobAndRefundAttemptAsync(
                indexingJob.DocumentId,
                indexingJob,
                CancellationToken.None);

            if (released)
            {
                logger.LogWarning(
                    "Released indexing job after schema readiness failure. DocumentId={DocumentId} IndexingJobId={IndexingJobId}",
                    indexingJob.DocumentId,
                    indexingJob.Id);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to release indexing job after schema readiness failure. DocumentId={DocumentId} IndexingJobId={IndexingJobId} ExceptionType={ExceptionType}",
                indexingJob.DocumentId,
                indexingJob.Id,
                exception.GetType().Name);
        }
    }
}
