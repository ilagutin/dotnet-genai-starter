namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// Frozen quality thresholds a baseline run must meet. <paramref name="K" /> is the
/// retrieval depth used for every query in the run.
/// </summary>
public sealed record RetrievalBaselineGates(
    double MinRecallAtK,
    double MinNoMatchAccuracy,
    int K)
{
    public const int MinDepth = 1;
    public const int MaxDepth = 50;
}
