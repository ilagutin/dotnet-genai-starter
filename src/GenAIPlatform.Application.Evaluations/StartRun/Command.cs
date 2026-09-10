using GenAIPlatform.Application.Core.Dispatching;

namespace GenAIPlatform.Application.Evaluations.StartRun;

public sealed record StartEvaluationRunCommand(
    string? DatasetVersion = null,
    string? Model = "evaluation",
    double? Temperature = 0,
    int? MaxOutputTokens = 256,
    int? TopK = null,
    double? MinSimilarityScore = null,
    string? CorrelationId = null)
    : IRequest<EvaluationRunResult>;
