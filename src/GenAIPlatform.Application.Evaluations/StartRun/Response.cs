using GenAIPlatform.Domain.Evaluations;

namespace GenAIPlatform.Application.Evaluations.StartRun;

public sealed record EvaluationRunResult(
    Guid RunId,
    string DatasetVersion,
    string RunnerVersion,
    string PromptVersion,
    string Model,
    string ModelSettings,
    string RetrievalConfiguration,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<EvaluationCaseResult> Cases);
