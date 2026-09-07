using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Evaluations.StartRun;
using GenAIPlatform.Domain.Evaluations;

namespace GenAIPlatform.Infrastructure.Evaluations;

internal sealed class PostgresEvaluationRunRepository
    : IEvaluationRunRepository
{
    private readonly PostgresEvaluationRunStore runStore;
    private readonly PostgresEvaluationCaseStore caseStore;
    private readonly PostgresEvaluationSummaryReader summaryReader;

    public PostgresEvaluationRunRepository(
        PostgresEvaluationConnectionFactory connectionFactory)
    {
        runStore = new PostgresEvaluationRunStore(connectionFactory);
        caseStore = new PostgresEvaluationCaseStore(connectionFactory);
        summaryReader = new PostgresEvaluationSummaryReader(
            runStore,
            caseStore);
    }

    public async Task AddRunAsync(
        EvaluationRunResult run,
        string tenantId,
        string userId,
        CancellationToken cancellationToken)
    {
        await runStore.AddRunAsync(
            run,
            tenantId,
            userId,
            cancellationToken);
    }

    public async Task AddCaseResultAsync(
        Guid runId,
        EvaluationCaseResult result,
        CancellationToken cancellationToken)
    {
        await caseStore.AddCaseResultAsync(
            runId,
            result,
            cancellationToken);
    }

    public async Task CompleteRunAsync(
        Guid runId,
        string status,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await runStore.CompleteRunAsync(
            runId,
            status,
            completedAtUtc,
            cancellationToken);
    }

    public async Task<EvaluationRunResult?> GetRunAsync(
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
        return run with { Cases = cases };
    }

    public async Task<EvaluationRunSummary?> GetSummaryAsync(
        Guid runId,
        string tenantId,
        string userId,
        CancellationToken cancellationToken)
    {
        return await summaryReader.GetSummaryAsync(
            runId,
            tenantId,
            userId,
            cancellationToken);
    }
}
