namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// Aggregated retrieval quality for one baseline run. Means are taken over recall
/// eligible queries only, which are the queries that label at least one relevant
/// document.
/// </summary>
public sealed record RetrievalMetrics(
    int QueryCount,
    int RecallEligibleQueryCount,
    double MeanRecallAtK,
    double MeanReciprocalRank,
    int NoMatchQueryCount,
    double NoMatchAccuracy,
    IReadOnlyDictionary<string, int> QueryCountsByCategory,
    IReadOnlyDictionary<string, int> HitCountsByCategory);
