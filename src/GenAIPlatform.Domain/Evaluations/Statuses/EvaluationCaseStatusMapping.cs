namespace GenAIPlatform.Domain.Evaluations;

public static class EvaluationCaseStatusMapping
{
    public static string ToPublicValue(this EvaluationCaseStatus status) => status switch
    {
        EvaluationCaseStatus.Passed => "Passed",
        EvaluationCaseStatus.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Undefined EvaluationCaseStatus value.")
    };
}
