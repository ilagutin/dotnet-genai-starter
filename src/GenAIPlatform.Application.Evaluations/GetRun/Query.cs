using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Evaluations.StartRun;

namespace GenAIPlatform.Application.Evaluations;

public sealed record GetEvaluationRunQuery(Guid RunId)
    : IRequest<EvaluationRunResult?>;
