using System.Runtime.ExceptionServices;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Security;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents;

internal sealed class UploadDocumentHandler(
    IDocumentStorage documentStorage,
    IDocumentMetadataRepository repository,
    UploadDocumentNormalizer normalizer,
    IUserContext userContext,
    DocumentUploadFactory uploadFactory,
    DocumentUploadRollbackInvoker rollbackInvoker,
    IOptions<DocumentIngestionOptions> options)
    : IRequestHandler<UploadDocumentCommand, UploadDocumentResponse>
{
    public async Task<UploadDocumentResponse> HandleAsync(
        UploadDocumentCommand request,
        CancellationToken cancellationToken)
    {
        var currentUserId = userContext.RequireAuthenticatedUser();
        var currentTenantId = userContext.RequireAuthenticatedTenant();
        var validatedUpload = normalizer.Normalize(request);

        var documentId = Guid.NewGuid();
        StoredDocument storedDocument;
        try
        {
            storedDocument = await documentStorage.SaveAsync(
                documentId,
                validatedUpload.FileName,
                request.Content,
                options.Value.MaxUploadBytes,
                cancellationToken);
        }
        catch (DocumentStorageLimitExceededException exception)
        {
            throw new DocumentTooLargeException(exception.Message);
        }

        // Post-save validation must roll back the staged file on failure: SaveAsync has
        // already written content to disk and the document metadata has not been recorded,
        // so without rollback we would leak an orphaned file (visible to operators, not
        // referenced by the repository).
        try
        {
            if (storedDocument.SizeBytes <= 0)
            {
                throw new DocumentValidationException("Document file is empty.");
            }

            var maxUploadBytes = options.Value.MaxUploadBytes;
            if (maxUploadBytes > 0 && storedDocument.SizeBytes > maxUploadBytes)
            {
                throw new DocumentTooLargeException(
                    $"Document file must be {maxUploadBytes} bytes or fewer.");
            }
        }
        catch (Exception primaryException)
        {
            await rollbackInvoker.InvokePreservingPrimaryAsync(
                documentId,
                storedDocument,
                DocumentUploadRollbackState.StorageNotCommitted,
                primaryException);
            ExceptionDispatchInfo.Capture(primaryException).Throw();
            throw; // unreachable
        }

        DocumentUploadArtifacts artifacts;
        try
        {
            artifacts = uploadFactory.Create(
                documentId,
                currentTenantId,
                currentUserId,
                request,
                validatedUpload,
                storedDocument);
        }
        catch (Exception primaryException)
        {
            await rollbackInvoker.InvokePreservingPrimaryAsync(
                documentId,
                storedDocument,
                DocumentUploadRollbackState.StorageNotCommitted,
                primaryException);
            ExceptionDispatchInfo.Capture(primaryException).Throw();
            throw; // unreachable
        }

        try
        {
            await documentStorage.CommitAsync(storedDocument, CancellationToken.None);
        }
        catch (Exception primaryException)
        {
            await rollbackInvoker.InvokePreservingPrimaryAsync(
                documentId,
                storedDocument,
                DocumentUploadRollbackState.StorageNotCommitted,
                primaryException);
            ExceptionDispatchInfo.Capture(primaryException).Throw();
            throw; // unreachable
        }

        var rollbackState = DocumentUploadRollbackState.RepositoryCreateNotStarted;
        try
        {
            await CreateDocumentMetadataAsync(
                artifacts,
                state => rollbackState = state,
                cancellationToken);
        }
        catch (Exception primaryException)
        {
            await rollbackInvoker.InvokePreservingPrimaryAsync(
                documentId,
                storedDocument,
                rollbackState,
                primaryException);
            ExceptionDispatchInfo.Capture(primaryException).Throw();
            throw; // unreachable
        }

        return ToResponse(artifacts);
    }

    private async Task CreateDocumentMetadataAsync(
        DocumentUploadArtifacts artifacts,
        Action<DocumentUploadRollbackState> setRollbackState,
        CancellationToken cancellationToken)
    {
        Task createTask;
        try
        {
            createTask = repository.CreateDocumentWithJobAsync(
                artifacts.Document,
                artifacts.IndexingJob,
                cancellationToken);
        }
        catch (DocumentMetadataNotCommittedException)
        {
            setRollbackState(DocumentUploadRollbackState.MetadataNotCommitted);
            throw;
        }

        setRollbackState(DocumentUploadRollbackState.MetadataOutcomeUnknown);

        try
        {
            await createTask;
        }
        catch (DocumentMetadataNotCommittedException)
        {
            setRollbackState(DocumentUploadRollbackState.MetadataNotCommitted);
            throw;
        }
    }

    private static UploadDocumentResponse ToResponse(DocumentUploadArtifacts artifacts)
    {
        var document = artifacts.Document;
        var indexingJob = artifacts.IndexingJob;

        return new UploadDocumentResponse(
            document.Id,
            document.Title,
            document.FileName,
            document.Version,
            document.AccessLevel.ToString(),
            document.IndexingStatus.ToString(),
            indexingJob.Id,
            indexingJob.Status.ToString(),
            document.CreatedAtUtc);
    }
}
