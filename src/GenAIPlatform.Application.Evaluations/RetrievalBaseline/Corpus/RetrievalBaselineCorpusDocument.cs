namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;

/// <summary>
/// One embedded benchmark document ready to be written to the retrieval store.
/// <paramref name="DocumentId" /> is derived deterministically from
/// <paramref name="Id" />, and <paramref name="EmbeddingProvider" /> and
/// <paramref name="EmbeddingModel" /> are the embedding identity recorded on the
/// document's chunks, either of which is deliberately incompatible for
/// compatibility-filter fixtures.
/// </summary>
public sealed record RetrievalBaselineCorpusDocument(
    string Id,
    Guid DocumentId,
    string TenantId,
    string OwnerUserId,
    string AccessLevel,
    int Version,
    string Title,
    string FileName,
    string EmbeddingProvider,
    string EmbeddingModel,
    IReadOnlyList<RetrievalBaselineCorpusChunk> Chunks);
