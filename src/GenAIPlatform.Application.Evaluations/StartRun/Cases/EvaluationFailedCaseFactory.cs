using GenAIPlatform.Domain.Evaluations;

namespace GenAIPlatform.Application.Evaluations.StartRun.Cases;

internal sealed class EvaluationFailedCaseFactory(TimeProvider timeProvider)
{
    public EvaluationCaseResult Create(
        EvaluationCase evaluationCase,
        string errorCode,
        string errorMessage,
        long started)
    {
        return new EvaluationCaseResult(
            evaluationCase.Id,
            evaluationCase.Name,
            EvaluationCaseStatus.Failed.ToPublicValue(),
            Answer: null,
            RetrievedCount: 0,
            RetrievalHit: false,
            timeProvider.GetElapsedTime(started),
            EstimatedCost: 0,
            CostCurrency: "USD",
            errorCode,
            errorMessage,
            [new EvaluationCheckResult("runtime", false, errorMessage)]);
    }
}
