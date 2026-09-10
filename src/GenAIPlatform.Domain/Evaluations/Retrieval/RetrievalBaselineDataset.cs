namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// The frozen retrieval baseline dataset. Every tenant referenced by the corpus or the
/// queries must start with <paramref name="TenantPrefix" /> so a baseline run can only
/// ever replace isolated benchmark rows.
/// </summary>
public sealed record RetrievalBaselineDataset(
    string Version,
    string TenantPrefix,
    RetrievalBaselineGates Gates,
    IReadOnlyList<RetrievalBaselineDocument> Corpus,
    IReadOnlyList<RetrievalBaselineQuery> Queries);
