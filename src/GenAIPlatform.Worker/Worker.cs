using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Health;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Worker;

public sealed partial class Worker(
    ILogger<Worker> logger,
    IServiceScopeFactory serviceScopeFactory,
    IOptions<DocumentIngestionOptions> options)
    : BackgroundService
{
    private const int MaxConsecutiveErrorBackoffSteps = 3;

    private readonly string workerId = $"{Environment.MachineName}-{Guid.NewGuid():n}";
    private int consecutiveErrors;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await TryLogStartupStatusAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var cleanupSucceeded = await ProcessOrphanedDocumentStorageCleanupAsync(stoppingToken);
            var indexingSucceeded = await ProcessPendingIndexingJobsAsync(stoppingToken);
            consecutiveErrors = cleanupSucceeded && indexingSucceeded
                ? 0
                : Math.Min(consecutiveErrors + 1, MaxConsecutiveErrorBackoffSteps);

            await Task.Delay(GetPollDelay(), stoppingToken);
        }
    }

    private async Task TryLogStartupStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>();

            var health = await dispatcher.DispatchAsync<GetHealthStatusQuery, HealthStatus>(
                new GetHealthStatusQuery("worker"),
                cancellationToken);

            LogWorkerStarted(
                logger,
                health.Status,
                health.CheckedAtUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogStartupHealthCheckFailed(logger, exception);
        }
    }

    private async Task<bool> ProcessPendingIndexingJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>();
            var result = await dispatcher.DispatchAsync<ProcessIndexingJobsCommand, ProcessIndexingJobsResponse>(
                new ProcessIndexingJobsCommand(
                    workerId,
                    options.Value.MaxIndexingJobsPerPoll),
                cancellationToken);

            if (result.Claimed > 0 || result.ExpiredOrExhaustedFailed > 0)
            {
                LogIndexingJobsSummary(
                    logger,
                    result.Claimed,
                    result.Indexed,
                    result.Failed,
                    result.Retried,
                    result.ExpiredOrExhaustedFailed);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception exception)
        {
            LogIndexingJobsFailed(logger, exception);
            return false;
        }
    }

    private async Task<bool> ProcessOrphanedDocumentStorageCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>();
            var result = await dispatcher.DispatchAsync<ProcessDocumentStorageCleanupCommand, ProcessDocumentStorageCleanupResponse>(
                new ProcessDocumentStorageCleanupCommand(
                    workerId,
                    options.Value.MaxStorageCleanupRequestsPerPoll),
                cancellationToken);

            if (result.Discovered > 0 || result.Failed > 0)
            {
                LogStorageCleanupSummary(
                    logger,
                    result.Discovered,
                    result.Deleted,
                    result.Deferred,
                    result.Failed);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception exception)
        {
            LogStorageCleanupFailed(logger, exception);
            return false;
        }
    }

    private TimeSpan GetPollDelay()
    {
        return WorkerPollDelay.Calculate(
            options.Value.WorkerPollIntervalSeconds,
            consecutiveErrors,
            Random.Shared.NextDouble());
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "GenAIPlatform Worker started with application status {Status} at {CheckedAtUtc}")]
    private static partial void LogWorkerStarted(
        ILogger logger,
        string status,
        DateTimeOffset checkedAtUtc);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Worker startup health check failed. Polling will continue and report processing failures normally.")]
    private static partial void LogStartupHealthCheckFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Processed indexing jobs. Claimed={Claimed} Indexed={Indexed} Failed={Failed} Retried={Retried} ExpiredOrExhaustedFailed={ExpiredOrExhaustedFailed}")]
    private static partial void LogIndexingJobsSummary(
        ILogger logger,
        int claimed,
        int indexed,
        int failed,
        int retried,
        int expiredOrExhaustedFailed);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Error,
        Message = "Worker failed while processing indexing jobs.")]
    private static partial void LogIndexingJobsFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "Processed orphaned document storage cleanup. Discovered={Discovered} Deleted={Deleted} Deferred={Deferred} Failed={Failed}")]
    private static partial void LogStorageCleanupSummary(
        ILogger logger,
        int discovered,
        int deleted,
        int deferred,
        int failed);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Error,
        Message = "Worker failed while processing orphaned document storage cleanup.")]
    private static partial void LogStorageCleanupFailed(
        ILogger logger,
        Exception exception);
}
