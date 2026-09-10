namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// The retrieval settings a baseline run measured under. <paramref name="SettingsHash" />
/// is a SHA-256 digest of the other fields so two reports can be compared for
/// configuration drift in one step.
/// </summary>
public sealed record RetrievalBaselineConfiguration(
    string EmbeddingProvider,
    string EmbeddingModel,
    string MockVariant,
    int EmbeddingDimensions,
    int TopK,
    double MinSimilarityScore,
    string SettingsHash);
