namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// A labeled corpus chunk. <paramref name="DocumentVersion" /> is null for chunks that
/// belong to the document's current version and is set only for history rows that must
/// stay ineligible for retrieval.
/// </summary>
public sealed record RetrievalBaselineChunk(
    string Id,
    string Text,
    int? DocumentVersion = null);
