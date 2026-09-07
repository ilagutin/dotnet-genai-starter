using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents;

internal sealed class DocumentStorageCleanupRequestProcessor(
    IDocumentStorage documentStorage,
    IDocumentStorageCleanupRepository cleanupRepository,
    IDocumentIngestionRepository repository,
    IOptions<DocumentIngestionOptions> ingestionOptions,
    ILogger<DocumentStorageCleanupRequestProcessor> logger)
{
    private const string InvalidMetadataAbsenceProofFailureReason = "Metadata absence proof is invalid.";
    private const string MetadataVerificationFailureReason = "Failed to verify metadata absence.";
    private const string MetadataStillExistsFailureReason = "Document metadata still exists.";

    public async Task<DocumentStorageCleanupOutcome> ProcessAsync(
        DocumentStorageCleanupRequest cleanupRequest,
        CancellationToken cancellationToken)
    {
        if (!DocumentStorageCleanupProof.IsValid(cleanupRequest.MetadataAbsenceProof))
        {
            LogInvalidMetadataAbsenceProof(cleanupRequest);
            await cleanupRepository.FailAsync(cleanupRequest, InvalidMetadataAbsenceProofFailureReason, cancellationToken);
            return DocumentStorageCleanupOutcome.Failed;
        }

        var metadataState = await VerifyMetadataAbsenceAsync(cleanupRequest, cancellationToken);
        if (metadataState == DocumentStorageCleanupMetadataState.Unknown)
        {
            return await DeferOrFailAsync(cleanupRequest, MetadataVerificationFailureReason, cancellationToken);
        }

        if (metadataState == DocumentStorageCleanupMetadataState.Exists)
        {
            await cleanupRepository.DeferAsync(cleanupRequest, MetadataStillExistsFailureReason, GetRetryDelay(), cancellationToken);
            return DocumentStorageCleanupOutcome.Deferred;
        }

        return await DeleteStorageAndCompleteCleanupAsync(cleanupRequest, cancellationToken);
    }

    private async Task<DocumentStorageCleanupMetadataState> VerifyMetadataAbsenceAsync(
        DocumentStorageCleanupRequest cleanupRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            var documentExists = await repository.DocumentExistsAsync(cleanupRequest.DocumentId, cancellationToken);
            if (!documentExists)
            {
                return DocumentStorageCleanupMetadataState.Absent;
            }

            LogMetadataStillExists(cleanupRequest);
            return DocumentStorageCleanupMetadataState.Exists;
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                         !cancellationToken.IsCancellationRequested)
        {
            LogMetadataVerificationFailure(cleanupRequest, exception);
            return DocumentStorageCleanupMetadataState.Unknown;
        }
    }

    private async Task<DocumentStorageCleanupOutcome> DeleteStorageAndCompleteCleanupAsync(
        DocumentStorageCleanupRequest cleanupRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            await DeleteRecordedStoragePathsAsync(cleanupRequest, cancellationToken);

            var completed = await cleanupRepository.CompleteAsync(cleanupRequest, cancellationToken);
            if (!completed)
            {
                LogCleanupOwnershipLost(cleanupRequest);
                return DocumentStorageCleanupOutcome.Failed;
            }

            return DocumentStorageCleanupOutcome.Deleted;
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                         !cancellationToken.IsCancellationRequested)
        {
            LogCleanupFailure(cleanupRequest, exception);
            return await DeferOrFailAsync(cleanupRequest, exception.GetType().Name, cancellationToken);
        }
    }

    private async Task<DocumentStorageCleanupOutcome> DeferOrFailAsync(
        DocumentStorageCleanupRequest cleanupRequest,
        string failureReason,
        CancellationToken cancellationToken)
    {
        if (ShouldRetry(cleanupRequest))
        {
            await cleanupRepository.DeferAsync(cleanupRequest, failureReason, GetRetryDelay(), cancellationToken);
            return DocumentStorageCleanupOutcome.Deferred;
        }

        await cleanupRepository.FailAsync(cleanupRequest, failureReason, cancellationToken);
        return DocumentStorageCleanupOutcome.Failed;
    }

    private bool ShouldRetry(DocumentStorageCleanupRequest cleanupRequest)
    {
        var maxAttempts = Math.Max(1, ingestionOptions.Value.MaxStorageCleanupAttempts);
        return cleanupRequest.Attempts < maxAttempts;
    }

    private async Task DeleteRecordedStoragePathsAsync(
        DocumentStorageCleanupRequest cleanupRequest,
        CancellationToken cancellationToken)
    {
        await documentStorage.DeleteAsync(cleanupRequest.StoragePath, cancellationToken);

        if (string.IsNullOrWhiteSpace(cleanupRequest.StagedStoragePath) ||
            cleanupRequest.StagedStoragePath.Equals(cleanupRequest.StoragePath, StringComparison.Ordinal))
        {
            return;
        }

        await documentStorage.DeleteAsync(cleanupRequest.StagedStoragePath, cancellationToken);
    }

    private void LogInvalidMetadataAbsenceProof(DocumentStorageCleanupRequest cleanupRequest)
    {
        logger.LogWarning(
            "Skipped orphaned document cleanup because metadata absence proof is invalid. DocumentId={DocumentId} StoragePath={StoragePath} Proof={MetadataAbsenceProof}",
            cleanupRequest.DocumentId,
            cleanupRequest.StoragePath,
            cleanupRequest.MetadataAbsenceProof);
    }

    private void LogMetadataVerificationFailure(
        DocumentStorageCleanupRequest cleanupRequest,
        Exception exception)
    {
        logger.LogWarning(
            exception,
            "Failed to verify metadata absence before orphaned document cleanup. DocumentId={DocumentId} StoragePath={StoragePath}",
            cleanupRequest.DocumentId,
            cleanupRequest.StoragePath);
    }

    private void LogMetadataStillExists(DocumentStorageCleanupRequest cleanupRequest)
    {
        logger.LogWarning(
            "Deferred orphaned document cleanup because document metadata exists. DocumentId={DocumentId} StoragePath={StoragePath}",
            cleanupRequest.DocumentId,
            cleanupRequest.StoragePath);
    }

    private void LogCleanupFailure(
        DocumentStorageCleanupRequest cleanupRequest,
        Exception exception)
    {
        logger.LogWarning(
            exception,
            "Failed to process orphaned document cleanup. DocumentId={DocumentId} StoragePath={StoragePath}",
            cleanupRequest.DocumentId,
            cleanupRequest.StoragePath);
    }

    private void LogCleanupOwnershipLost(DocumentStorageCleanupRequest cleanupRequest)
    {
        logger.LogWarning(
            "Skipped orphaned document cleanup completion because worker ownership changed. DocumentId={DocumentId} StoragePath={StoragePath} WorkerId={WorkerId}",
            cleanupRequest.DocumentId,
            cleanupRequest.StoragePath,
            cleanupRequest.WorkerId);
    }

    private TimeSpan GetRetryDelay()
    {
        return TimeSpan.FromSeconds(Math.Max(0, ingestionOptions.Value.StorageCleanupRetryDelaySeconds));
    }
}
