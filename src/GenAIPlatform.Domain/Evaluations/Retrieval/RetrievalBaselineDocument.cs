namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// A synthetic benchmark document. <paramref name="EmbeddingProvider" /> and
/// <paramref name="EmbeddingModel" /> are null when the document is embedded with the
/// run's configured embedding identity, and are set only for documents that must be
/// excluded by the embedding compatibility filter: an overridden provider covers a
/// different vendor, an overridden model covers a different model of the same vendor.
/// </summary>
public sealed record RetrievalBaselineDocument(
    string Id,
    string TenantId,
    string OwnerUserId,
    string AccessLevel,
    int Version,
    string Title,
    string FileName,
    IReadOnlyList<RetrievalBaselineChunk> Chunks,
    string? EmbeddingProvider = null,
    string? EmbeddingModel = null);
