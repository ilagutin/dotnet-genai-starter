namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;

/// <summary>
/// The complete benchmark corpus for one run. <paramref name="TenantIds" /> is the exact,
/// closed set of tenants a store implementation may delete from and write to.
/// </summary>
public sealed record RetrievalBaselineCorpus(
    IReadOnlyList<string> TenantIds,
    IReadOnlyList<RetrievalBaselineCorpusDocument> Documents);
