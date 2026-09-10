using GenAIPlatform.Domain.Evaluations.Retrieval;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// A loaded baseline dataset together with the SHA-256 hex digest of the exact bytes it
/// was read from, so a report can prove which dataset produced it.
/// </summary>
public sealed record RetrievalBaselineDatasetSource(
    RetrievalBaselineDataset Dataset,
    string ContentHash);
