using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Mcp;

/// <summary>
/// Background connection activity for the external MCP connection manager: an optional startup
/// warmup followed by a periodic recovery pass that re-attempts servers which are not currently
/// available. Runs off the host startup path so a slow or unreachable server never blocks startup.
/// A failed pass is logged and retried on the next tick rather than tearing down the loop.
/// </summary>
internal sealed class ExternalMcpBackgroundRefresher(
    ExternalMcpConnector connector,
    ExternalMcpConnectionState state,
    IOptions<ExternalMcpOptions> options,
    ILogger logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (options.Value.ConnectOnStartup)
        {
            await SafeRefreshAsync(cancellationToken);
        }

        var interval = options.Value.RefreshInterval;
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SafeRefreshAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SafeRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await connector.RefreshAsync(state, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "External MCP background refresh pass failed; exception type {ExceptionType}.",
                exception.GetType().Name);
        }
    }
}
