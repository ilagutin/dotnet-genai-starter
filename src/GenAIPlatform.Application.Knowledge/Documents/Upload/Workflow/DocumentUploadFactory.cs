using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents;

internal sealed class DocumentUploadFactory(
    IOptions<DocumentIngestionOptions> options,
    TimeProvider timeProvider)
{
    public DocumentUploadArtifacts Create(
        Guid documentId,
        string tenantId,
        string userId,
        UploadDocumentCommand request,
        ValidatedDocumentUpload validatedUpload,
        StoredDocument storedDocument)
    {
        var now = timeProvider.GetUtcNow();
        var document = new Document(
            documentId,
            tenantId,
            userId,
            validatedUpload.FileName,
            ResolveTitle(request.Title, validatedUpload.FileName),
            NormalizeOptional(request.ContentType),
            validatedUpload.Extension,
            storedDocument.StoragePath,
            storedDocument.SizeBytes,
            storedDocument.ContentHash,
            Version: 1,
            validatedUpload.AccessLevel,
            DocumentIndexingStatus.PendingIndexing,
            now,
            now,
            FailureReason: null);
        var indexingJob = new IndexingJob(
            Guid.NewGuid(),
            document.Id,
            IndexingJobStatus.Pending,
            Attempts: 0,
            MaxAttempts: Math.Max(1, options.Value.MaxIndexingAttempts),
            now,
            now,
            AvailableAtUtc: now,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            WorkerId: null,
            FailureReason: null);

        return new DocumentUploadArtifacts(document, indexingJob);
    }

    private static string ResolveTitle(
        string? requestedTitle,
        string fileName)
    {
        var title = NormalizeOptional(requestedTitle);
        if (title is not null)
        {
            return title.Length <= 200 ? title : title[..200];
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
