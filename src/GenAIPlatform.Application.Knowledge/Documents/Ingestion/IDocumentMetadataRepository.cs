using GenAIPlatform.Domain.Documents;

namespace GenAIPlatform.Application.Knowledge.Documents;

public interface IDocumentMetadataRepository
{
    /// <summary>
    /// Creates document metadata and its first indexing job atomically.
    /// If this method throws <see cref="DocumentMetadataNotCommittedException" />, the
    /// implementation has proved that no document/job metadata was committed and callers may
    /// clean up committed storage. Any other exception means the durable outcome is unknown;
    /// callers must preserve committed storage for reconciliation instead of trusting an
    /// immediate metadata lookup.
    /// </summary>
    Task CreateDocumentWithJobAsync(
        Document document,
        IndexingJob indexingJob,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns whether durable document metadata exists.
    /// This is safe for reconciliation and diagnostics, but a single false result after an
    /// unknown create failure is not proof that a commit cannot still become visible.
    /// </summary>
    Task<bool> DocumentExistsAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    Task<Document?> GetDocumentForIndexingAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    Task<DocumentIndexingStatusSnapshot?> GetDocumentStatusAsync(
        Guid documentId,
        string tenantId,
        string? userId,
        CancellationToken cancellationToken);

}
