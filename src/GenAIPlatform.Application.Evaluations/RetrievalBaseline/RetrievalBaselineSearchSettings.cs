namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// The retrieval depth and similarity threshold every baseline query is run with.
/// </summary>
public sealed record RetrievalBaselineSearchSettings(
    string EmbeddingModel,
    int TopK,
    double MinSimilarityScore);
