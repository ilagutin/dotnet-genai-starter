using GenAIPlatform.Application.Evaluations.StartRun.Cases;
using GenAIPlatform.Application.Evaluations.StartRun.Context;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Domain.Evaluations;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.Application.Evaluations.StartRun;

internal sealed class EvaluationRunCompletionCoordinator(
    IEvaluationRunRepository runRepository,
    EvaluationCaseRunner caseRunner,
    TimeProvider timeProvider,
    ILogger<EvaluationRunCompletionCoordinator> logger)
{
    public async Task<EvaluationRunResult> RunCasesAndCompleteAsync(
        EvaluationRunResult run,
        IReadOnlyList<EvaluationCase> cases,
        ModelGatewayRequestSettings gateway,
        EvaluationRetrievalConfiguration retrievalConfig,
        string tenantId,
        string userId,
        CancellationToken cancellationToken)
    {
        var caseResults = new List<EvaluationCaseResult>();
        var finalStatus = EvaluationRunStatus.Succeeded;

        try
        {
            foreach (var evaluationCase in cases)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    finalStatus = EvaluationRunStatus.Canceled;
                    break;
                }

                var result = await caseRunner.RunAsync(
                    run.RunId,
                    evaluationCase,
                    gateway,
                    retrievalConfig,
                    tenantId,
                    userId,
                    cancellationToken);
                caseResults.Add(result);
                await runRepository.AddCaseResultAsync(
                    run.RunId,
                    result,
                    CancellationToken.None);

                if (result.Status != EvaluationCaseStatus.Passed.ToPublicValue())
                {
                    finalStatus = EvaluationRunStatus.Failed;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryMarkRunCanceledAsync(run.RunId);
            throw;
        }
        catch
        {
            await TryMarkRunFailedAsync(
                run.RunId,
                "evaluation case execution or persistence failure");
            throw;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            finalStatus = EvaluationRunStatus.Canceled;
        }

        var completedAtUtc = timeProvider.GetUtcNow();
        try
        {
            await runRepository.CompleteRunAsync(
                run.RunId,
                finalStatus.ToPublicValue(),
                completedAtUtc,
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryMarkRunCanceledAsync(run.RunId);
            throw;
        }
        catch (Exception exception) when (finalStatus == EvaluationRunStatus.Canceled)
        {
            logger.LogWarning(
                exception,
                "Failed to complete canceled evaluation run. RunId={RunId}",
                run.RunId);
            throw;
        }
        catch
        {
            await TryMarkRunFailedAsync(
                run.RunId,
                "evaluation run completion failure");
            throw;
        }

        return run with
        {
            Status = finalStatus.ToPublicValue(),
            CompletedAtUtc = completedAtUtc,
            Cases = caseResults
        };
    }

    private async Task TryMarkRunCanceledAsync(Guid runId)
    {
        try
        {
            await runRepository.CompleteRunAsync(
                runId,
                EvaluationRunStatus.Canceled.ToPublicValue(),
                timeProvider.GetUtcNow(),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to mark evaluation run canceled after cancellation. RunId={RunId}",
                runId);
        }
    }

    private async Task TryMarkRunFailedAsync(Guid runId, string originalExceptionContext)
    {
        try
        {
            await runRepository.CompleteRunAsync(
                runId,
                EvaluationRunStatus.Failed.ToPublicValue(),
                timeProvider.GetUtcNow(),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to mark evaluation run failed after {OriginalExceptionContext}. RunId={RunId}",
                originalExceptionContext,
                runId);
        }
    }
}
