using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Evaluations.StartRun;
using GenAIPlatform.Domain.Evaluations;

namespace GenAIPlatform.Infrastructure.Evaluations;

internal sealed class PostgresEvaluationSummaryReader(
    PostgresEvaluationRunStore runStore,
    PostgresEvaluationCaseStore caseStore)
{
    public async Task<EvaluationRunSummary?> GetSummaryAsync(
        Guid runId,
        string tenantId,
        string userId,
        CancellationToken cancellationToken)
    {
        var run = await runStore.GetRunAsync(
            runId,
            tenantId,
            userId,
            cancellationToken);
        if (run is null)
        {
            return null;
        }

        var cases = await caseStore.ReadCasesAsync(
            runId,
            cancellationToken);
        return CreateSummary(run with { Cases = cases });
    }

    private static EvaluationRunSummary CreateSummary(EvaluationRunResult run)
    {
        var passedStatus = EvaluationCaseStatus.Passed.ToPublicValue();
        var total = run.Cases.Count;
        var passed = run.Cases.Count(result => result.Status == passedStatus);
        var failed = total - passed;
        var currency = run.Cases
            .Select(static result => result.CostCurrency)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? "USD";

        return new EvaluationRunSummary(
            run.RunId,
            run.DatasetVersion,
            run.Status,
            total,
            passed,
            failed,
            total == 0 ? 0 : run.Cases.Count(static result => result.RetrievalHit) / (double)total,
            total == 0 ? 0 : run.Cases.Average(static result => result.Latency.TotalMilliseconds),
            total == 0 ? 0 : run.Cases.Average(static result => result.EstimatedCost),
            currency,
            run.Cases
                .Where(result => result.Status != passedStatus)
                .Select(static result => new EvaluationFailedCase(
                    result.CaseId,
                    result.Name,
                    result.Status,
                    result.ErrorCode,
                    result.Checks))
                .ToArray());
    }
}
