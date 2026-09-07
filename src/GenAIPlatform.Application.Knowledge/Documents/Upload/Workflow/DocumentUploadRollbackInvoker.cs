using Microsoft.Extensions.Logging;

namespace GenAIPlatform.Application.Knowledge.Documents;

/// <summary>
/// Invokes the rollback coordinator while preserving the primary upload exception on the
/// throw chain. If the rollback itself fails, the secondary exception is captured via
/// structured logging (with primary-exception context) and intentionally not propagated:
/// the primary failure is the user-visible root cause and must reach the API exception
/// handler so the response carries the correct status code.
/// </summary>
internal sealed partial class DocumentUploadRollbackInvoker(
    DocumentUploadRollbackCoordinator coordinator,
    ILogger<DocumentUploadRollbackInvoker> logger)
{
    public async Task InvokePreservingPrimaryAsync(
        Guid documentId,
        StoredDocument storedDocument,
        DocumentUploadRollbackState state,
        Exception primaryException)
    {
        try
        {
            await coordinator.HandleFailureAsync(documentId, storedDocument, state);
        }
        catch (Exception rollbackException)
        {
            LogRollbackFailedAfterPrimary(
                logger,
                rollbackException,
                documentId,
                storedDocument.StoragePath,
                state,
                primaryException.GetType().FullName,
                primaryException.Message);
        }
    }

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Error,
        Message = "Document upload rollback failed after primary failure. Primary error preserved for re-throw. DocumentId={DocumentId} StoragePath={StoragePath} RollbackState={RollbackState} PrimaryType={PrimaryType} PrimaryMessage={PrimaryMessage}")]
    private static partial void LogRollbackFailedAfterPrimary(
        ILogger logger,
        Exception exception,
        Guid documentId,
        string storagePath,
        DocumentUploadRollbackState rollbackState,
        string? primaryType,
        string primaryMessage);
}
