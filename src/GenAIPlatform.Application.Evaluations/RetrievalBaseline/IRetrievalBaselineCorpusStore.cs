using GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// Owns the isolated benchmark corpus used by the retrieval baseline. Implementations must
/// restrict every delete and insert to the tenants named in
/// <see cref="RetrievalBaselineCorpus.TenantIds" /> and must normalize persistence
/// failures into <see cref="RetrievalBaselineStoreException" />.
/// </summary>
public interface IRetrievalBaselineCorpusStore
{
    /// <summary>
    /// Replaces every benchmark document and chunk for the corpus tenants so a run always
    /// measures the frozen dataset and nothing left over from an earlier run.
    /// </summary>
    Task ReplaceCorpusAsync(
        RetrievalBaselineCorpus corpus,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the store version metadata recorded in the baseline report.
    /// </summary>
    Task<RetrievalBaselineEnvironment> GetEnvironmentAsync(CancellationToken cancellationToken);
}
