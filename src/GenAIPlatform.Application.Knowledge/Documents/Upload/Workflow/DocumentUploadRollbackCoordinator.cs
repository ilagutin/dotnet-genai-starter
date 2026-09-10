using Microsoft.Extensions.Logging;

namespace GenAIPlatform.Application.Knowledge.Documents;

internal sealed partial class DocumentUploadRollbackCoordinator(
    IDocumentStorage documentStorage,
    IDocumentStorageCleanupRepository cleanupRepository,
    ILogger<DocumentUploadRollbackCoordinator> logger,
    TimeProvider timeProvider)
{
    public async Task HandleFailureAsync(
        Guid documentId,
        StoredDocument storedDocument,
        DocumentUploadRollbackState state)
    {
        var metadataAbsenceProof = GetMetadataAbsenceProof(state);

        if (metadataAbsenceProof is not null)
        {
            await DeleteStoredDocumentIfPresentAsync(
                documentId,
                storedDocument,
                metadataAbsenceProof);
            return;
        }

        LogStoredDocumentPreserved(
            logger,
            documentId,
            storedDocument.StoragePath);
    }

    internal static string? GetMetadataAbsenceProof(DocumentUploadRollbackState state)
    {
        return state switch
        {
            DocumentUploadRollbackState.StorageNotCommitted =>
                DocumentStorageCleanupProof.StorageNotCommitted,
            DocumentUploadRollbackState.RepositoryCreateNotStarted =>
                DocumentStorageCleanupProof.RepositoryCreateNotStarted,
            DocumentUploadRollbackState.MetadataNotCommitted =>
                DocumentStorageCleanupProof.MetadataNotCommitted,
            DocumentUploadRollbackState.MetadataOutcomeUnknown => null,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown document upload rollback state.")
        };
    }

    private async Task DeleteStoredDocumentIfPresentAsync(
        Guid documentId,
        StoredDocument storedDocument,
        string metadataAbsenceProof)
    {
        try
        {
            await documentStorage.DeleteAsync(storedDocument.StoragePath, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogStoredDocumentDeleteFailed(
                logger,
                exception,
                storedDocument.StoragePath);
            await RecordOrphanedCleanupAsync(
                documentId,
                storedDocument,
                metadataAbsenceProof,
                exception);
        }
    }

    private async Task RecordOrphanedCleanupAsync(
        Guid documentId,
        StoredDocument storedDocument,
        string metadataAbsenceProof,
        Exception deleteException)
    {
        try
        {
            await cleanupRepository.RecordAsync(
                new DocumentStorageCleanupRequest(
                    documentId,
                    storedDocument.StoragePath,
                    storedDocument.StagedStoragePath,
                    storedDocument.ContentHash,
                    storedDocument.SizeBytes,
                    metadataAbsenceProof,
                    timeProvider.GetUtcNow(),
                    deleteException.GetType().Name),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogOrphanedCleanupRecordFailed(
                logger,
                exception,
                documentId,
                storedDocument.StoragePath);
            throw new DocumentStorageCleanupRecordingException(
                documentId,
                storedDocument.StoragePath,
                metadataAbsenceProof,
                deleteException,
                exception);
        }
    }

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "Preserved stored document after upload failure because document metadata outcome is unknown. DocumentId={DocumentId} StoragePath={StoragePath}")]
    private static partial void LogStoredDocumentPreserved(
        ILogger logger,
        Guid documentId,
        string storagePath);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Warning,
        Message = "Failed to delete stored document after upload rollback. StoragePath={StoragePath}")]
    private static partial void LogStoredDocumentDeleteFailed(
        ILogger logger,
        Exception exception,
        string storagePath);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Error,
        Message = "Failed to record orphaned document storage cleanup after upload rollback. DocumentId={DocumentId} StoragePath={StoragePath}")]
    private static partial void LogOrphanedCleanupRecordFailed(
        ILogger logger,
        Exception exception,
        Guid documentId,
        string storagePath);
}
