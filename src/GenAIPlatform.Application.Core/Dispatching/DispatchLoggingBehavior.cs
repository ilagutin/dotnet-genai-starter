using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.Application.Core.Dispatching;

internal sealed partial class DispatchLoggingBehavior<TRequest, TResponse>(
    ILogger<DispatchLoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await next();
            LogDispatched(logger, typeof(TRequest).Name, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception exception)
        {
            LogDispatchFailed(logger, exception, typeof(TRequest).Name, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Debug,
        Message = "Dispatched {RequestType} in {ElapsedMs}ms")]
    private static partial void LogDispatched(
        ILogger logger,
        string requestType,
        long elapsedMs);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Dispatch of {RequestType} failed after {ElapsedMs}ms")]
    private static partial void LogDispatchFailed(
        ILogger logger,
        Exception exception,
        string requestType,
        long elapsedMs);
}
