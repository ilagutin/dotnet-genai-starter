using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Evaluations.StartRun;
using GenAIPlatform.Domain.Evaluations;

namespace GenAIPlatform.Evaluations;

public static class EvaluationCliRunner
{
    public static async Task<int> RunAsync(
        IApplicationDispatcher dispatcher,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(CorrelationId: "cli-evaluation-smoke"),
            cancellationToken);

        var passedStatus = EvaluationCaseStatus.Passed.ToPublicValue();
        var succeededStatus = EvaluationRunStatus.Succeeded.ToPublicValue();
        var passed = result.Cases.Count(evaluationCase => evaluationCase.Status == passedStatus);
        output.WriteLine($"Evaluation run {result.RunId}");
        output.WriteLine($"Dataset: {result.DatasetVersion}");
        output.WriteLine($"Status: {result.Status}");
        output.WriteLine($"Passed: {passed}/{result.Cases.Count}");

        return result.Status == succeededStatus ? 0 : 1;
    }
}
