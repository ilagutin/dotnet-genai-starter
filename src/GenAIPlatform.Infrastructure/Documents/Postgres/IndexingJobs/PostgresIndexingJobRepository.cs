using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Domain.Documents;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.IndexingJobs;

internal sealed class PostgresIndexingJobRepository(
    PostgresIndexingJobClaimStore claimStore,
    PostgresIndexingJobLeaseStore leaseStore,
    PostgresIndexingJobFailureStore failureStore,
    PostgresIndexingJobCompletionStore completionStore) : IIndexingJobRepository
{
    public Task<IndexingJob?> ClaimNextPendingJobAsync(
        string workerId, TimeSpan processingLeaseDuration, CancellationToken cancellationToken) =>
        claimStore.ClaimNextPendingJobAsync(workerId, processingLeaseDuration, cancellationToken);

    public Task<int> MarkExpiredIndexingJobsFailedAsync(
        TimeSpan processingLeaseDuration, CancellationToken cancellationToken) =>
        claimStore.MarkExpiredIndexingJobsFailedAsync(processingLeaseDuration, cancellationToken);

    public Task<bool> RenewProcessingLeaseAsync(
        Guid documentId, IndexingJob indexingJob, CancellationToken cancellationToken) =>
        leaseStore.RenewProcessingLeaseAsync(documentId, indexingJob, cancellationToken);

    public Task<bool> ReplaceChunksAndCompleteIndexingAsync(
        Document document, IndexingJob indexingJob, IReadOnlyCollection<DocumentChunk> chunks,
        CancellationToken cancellationToken) =>
        completionStore.ReplaceChunksAndCompleteIndexingAsync(document, indexingJob, chunks, cancellationToken);

    public Task<bool> MarkIndexingFailedAsync(
        Guid documentId, IndexingJob indexingJob, string failureReason, bool retry,
        TimeSpan retryDelay, CancellationToken cancellationToken) =>
        failureStore.MarkIndexingFailedAsync(documentId, indexingJob, failureReason, retry, retryDelay, cancellationToken);

    public Task<bool> ReleaseProcessingJobAndRefundAttemptAsync(
        Guid documentId, IndexingJob indexingJob, CancellationToken cancellationToken) =>
        leaseStore.ReleaseProcessingJobAndRefundAttemptAsync(documentId, indexingJob, cancellationToken);
}
