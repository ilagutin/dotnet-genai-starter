namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// The full retrieval baseline report. It is safe to commit: it carries dataset ids,
/// hashes, settings, versions and metrics, and never question text, document text,
/// embedding vectors, credentials or connection strings.
/// </summary>
public sealed record RetrievalBaselineReport(
    string DatasetVersion,
    string DatasetHash,
    string CodeRevision,
    DateTimeOffset GeneratedAtUtc,
    RetrievalBaselineConfiguration Configuration,
    RetrievalBaselineEnvironment Environment,
    RetrievalBaselineAggregates Aggregates,
    IReadOnlyList<RetrievalBaselineGateResult> Gates,
    bool GatesPassed,
    IReadOnlyList<RetrievalBaselineQueryReport> Queries,
    RetrievalBaselineTimings Timings);
