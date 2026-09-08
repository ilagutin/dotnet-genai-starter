namespace GenAIPlatform.Domain.Evaluations;

public static class EvaluationRunStatusMapping
{
    public static string ToPublicValue(this EvaluationRunStatus status) => status switch
    {
        EvaluationRunStatus.Running => "Running",
        EvaluationRunStatus.Succeeded => "Succeeded",
        EvaluationRunStatus.Failed => "Failed",
        EvaluationRunStatus.Canceled => "Canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Undefined EvaluationRunStatus value.")
    };
}
