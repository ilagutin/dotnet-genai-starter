namespace GenAIPlatform.Application.Evaluations.StartRun;

public sealed record StartEvaluationRunValidationResult(
    string TenantId,
    string UserId,
    int TopK,
    double MinSimilarityScore);
