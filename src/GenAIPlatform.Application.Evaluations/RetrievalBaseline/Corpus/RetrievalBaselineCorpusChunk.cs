namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;

/// <summary>
/// One embedded benchmark chunk ready to be written to the retrieval store.
/// <paramref name="ChunkId" /> is derived deterministically from
/// <paramref name="Id" /> so repeated runs address the same rows.
/// </summary>
public sealed record RetrievalBaselineCorpusChunk(
    string Id,
    Guid ChunkId,
    int DocumentVersion,
    int Position,
    string Text,
    IReadOnlyList<float> Embedding,
    int? EmbeddingInputTokens);
