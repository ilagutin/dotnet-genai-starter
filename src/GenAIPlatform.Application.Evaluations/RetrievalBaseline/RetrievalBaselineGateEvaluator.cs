using GenAIPlatform.Domain.Evaluations.Retrieval;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// Compares measured retrieval quality against the dataset's frozen gates. A gate is met
/// only when the measured value reaches the threshold, so a regression is a failure rather
/// than a note in a report.
/// </summary>
internal sealed class RetrievalBaselineGateEvaluator
{
    public IReadOnlyList<RetrievalBaselineGateResult> Evaluate(
        RetrievalBaselineGates gates,
        RetrievalMetrics metrics)
    {
        return
        [
            new RetrievalBaselineGateResult(
                RetrievalBaselineGateResult.RecallGateName,
                gates.MinRecallAtK,
                metrics.MeanRecallAtK,
                metrics.MeanRecallAtK >= gates.MinRecallAtK),
            new RetrievalBaselineGateResult(
                RetrievalBaselineGateResult.NoMatchGateName,
                gates.MinNoMatchAccuracy,
                metrics.NoMatchAccuracy,
                metrics.NoMatchAccuracy >= gates.MinNoMatchAccuracy)
        ];
    }
}
