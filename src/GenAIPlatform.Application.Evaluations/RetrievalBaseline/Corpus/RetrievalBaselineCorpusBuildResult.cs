namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;

/// <summary>
/// The embedded corpus plus the embedding identity the run actually observed, which is
/// what the report and the retrieval compatibility filter are keyed on.
/// </summary>
public sealed record RetrievalBaselineCorpusBuildResult(
    RetrievalBaselineCorpus Corpus,
    string EmbeddingProvider,
    string EmbeddingModel,
    int EmbeddingDimensions,
    IReadOnlyDictionary<Guid, string> DocumentIdsByRowId);
