namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// Run-level retrieval quality. Means cover recall eligible queries only, which are the
/// queries labeling at least one relevant document.
/// </summary>
public sealed record RetrievalBaselineAggregates(
    int QueryCount,
    int RecallEligibleQueryCount,
    double RecallAtK,
    double MeanReciprocalRank,
    int NoMatchQueryCount,
    double NoMatchAccuracy,
    IReadOnlyDictionary<string, int> QueryCountsByCategory,
    IReadOnlyDictionary<string, int> HitCountsByCategory);
