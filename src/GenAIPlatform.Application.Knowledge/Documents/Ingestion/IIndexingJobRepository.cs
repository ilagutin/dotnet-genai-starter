using GenAIPlatform.Domain.Documents;

namespace GenAIPlatform.Application.Knowledge.Documents;

public interface IIndexingJobRepository
{
    /// <summary>
    /// Claims the next available job. Durable implementations must use an authoritative
    /// persistence clock, or an explicitly enforced skew policy, for lease expiry decisions.
    /// </summary>
    Task<IndexingJob?> ClaimNextPendingJobAsync(
        string workerId,
        TimeSpan processingLeaseDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fails exhausted or expired jobs. Durable implementations must use the same authoritative
    /// lease clock as <see cref="ClaimNextPendingJobAsync" />.
    /// </summary>
    Task<int> MarkExpiredIndexingJobsFailedAsync(
        TimeSpan processingLeaseDuration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Renews the owned processing lease using the durable lease clock.
    /// </summary>
    Task<bool> RenewProcessingLeaseAsync(
        Guid documentId,
        IndexingJob indexingJob,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces chunks for the claimed document version and completes the owned indexing job.
    /// Returns <see langword="true" /> when completion was committed, or <see langword="false" />
    /// when the job is no longer owned by the supplied worker/attempt and no completion was
    /// committed by this call. If this method throws
    /// <see cref="DocumentIndexingCompletionUnknownException" />, completion may already have
    /// committed and callers must not record retry or failure unless a later durable read proves
    /// completion did not commit. Any other exception must be thrown only before the completion
    /// commit outcome can be durable.
    /// </summary>
    Task<bool> ReplaceChunksAndCompleteIndexingAsync(
        Document document,
        IndexingJob indexingJob,
        IReadOnlyCollection<DocumentChunk> chunks,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records retry or terminal failure using the durable lease clock for retry scheduling.
    /// </summary>
    Task<bool> MarkIndexingFailedAsync(
        Guid documentId,
        IndexingJob indexingJob,
        string failureReason,
        bool retry,
        TimeSpan retryDelay,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns an owned processing job to the queue and refunds the claim attempt.
    /// Use only when no durable indexing output, provider billing, or other indexing side
    /// effect was committed and retrying the same job is required for a non-document-specific
    /// condition such as cancellation before side effects or a required schema-readiness failure.
    /// After any storage/provider side effect may have occurred, use <see cref="MarkIndexingFailedAsync" />
    /// so retry accounting records the consumed attempt.
    /// </summary>
    Task<bool> ReleaseProcessingJobAndRefundAttemptAsync(
        Guid documentId,
        IndexingJob indexingJob,
        CancellationToken cancellationToken);
}
