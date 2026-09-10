namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// One frozen gate compared against what the run measured. The gate names are part of the
/// report contract and stay stable across dataset versions.
/// </summary>
public sealed record RetrievalBaselineGateResult(
    string Name,
    double Threshold,
    double Actual,
    bool Met)
{
    public const string RecallGateName = "recall_at_k";
    public const string NoMatchGateName = "no_match_accuracy";
}
