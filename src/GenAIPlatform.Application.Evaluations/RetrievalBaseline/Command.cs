using GenAIPlatform.Application.Core.Dispatching;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// Runs the frozen retrieval quality baseline against the configured PostgreSQL retrieval
/// store. The run performs no model completion.
/// </summary>
public sealed record RunRetrievalBaselineCommand(
    string? DatasetVersion = null,
    string? CodeRevision = null)
    : IRequest<RetrievalBaselineReport>;
