using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed class ExternalMcpConnectionManager(
    IOptions<ExternalMcpOptions> options,
    IExternalMcpClientFactory clientFactory,
    IExternalMcpConnectionPolicy policy,
    ILogger<ExternalMcpConnectionManager> logger) : IExternalMcpConnectionManager
{
    private readonly object gate = new();
    private readonly ExternalMcpConnectionState state = new();
    private readonly ExternalMcpConnector connector = new(options, clientFactory, policy, logger);
    private readonly CancellationTokenSource lifetime = new();
    private Task background = Task.CompletedTask;
    private TaskCompletionSource? drained;
    private int activeOperations;
    private bool stopping;
    private bool started;

    internal Task BackgroundActivity => background;

    internal void Start()
    {
        lock (gate)
        {
            if (started || stopping)
            {
                return;
            }

            started = true;
            var refresher = new ExternalMcpBackgroundRefresher(connector, state, options, logger);
            background = Task.Run(() => refresher.RunAsync(lifetime.Token), CancellationToken.None);
        }
    }

    public IReadOnlyList<ExternalMcpServerSnapshot> GetSnapshots() => state.GetSnapshots();

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!TryEnterOperation())
        {
            throw new InvalidOperationException("External MCP is shutting down.");
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            await connector.RefreshAsync(state, linked.Token);
        }
        finally
        {
            ExitOperation();
        }
    }

    public async Task<ExternalMcpToolCallResult> CallToolAsync(
        ExternalMcpToolSnapshot tool,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        if (!TryEnterOperation())
        {
            return ExternalMcpToolCallResult.Unavailable("External MCP is shutting down.");
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            return await CallCoreAsync(tool, arguments, linked.Token, cancellationToken);
        }
        finally
        {
            ExitOperation();
        }
    }

    internal async Task ShutdownAsync()
    {
        Task drain;
        lock (gate)
        {
            stopping = true;
            state.StopAcceptingConnections();
            drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (activeOperations == 0)
            {
                drained.TrySetResult();
            }

            drain = drained.Task;
        }

        await lifetime.CancelAsync();
        await ObserveCancellationAsync(background);
        await drain;
        await Task.WhenAll(state.ClearConnections().Select(connection => connection.DisposeSafelyAsync(logger)));
        lifetime.Dispose();
    }

    private async Task<ExternalMcpToolCallResult> CallCoreAsync(
        ExternalMcpToolSnapshot tool,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken lifetimeToken,
        CancellationToken callerToken)
    {
        var connection = await connector.GetOrReconnectAsync(state, tool.ServerName, lifetimeToken);
        if (connection is null)
        {
            return ExternalMcpToolCallResult.Unavailable("External MCP server is unavailable.");
        }

        var firstAttempt = await connection.TryCallAsync(tool, arguments, lifetimeToken, callerToken, lifetime.Token, logger);
        if (firstAttempt.MarkUnavailable)
        {
            await MarkUnavailableAsync(tool.ServerName);
        }

        if (firstAttempt.Result is not null)
        {
            return firstAttempt.Result;
        }

        connection = await connector.GetOrReconnectAsync(state, tool.ServerName, lifetimeToken);
        if (connection is null)
        {
            return ExternalMcpToolCallResult.Unavailable("External MCP server is unavailable after reconnect.");
        }

        var secondAttempt = await connection.TryCallAsync(
            tool,
            arguments,
            lifetimeToken,
            callerToken,
            lifetime.Token,
            logger);
        if (secondAttempt.MarkUnavailable)
        {
            await MarkUnavailableAsync(tool.ServerName);
        }

        return secondAttempt.Result
            ?? ExternalMcpToolCallResult.Unavailable("External MCP tool call failed after reconnect.");
    }

    private bool TryEnterOperation()
    {
        lock (gate)
        {
            if (stopping)
            {
                return false;
            }

            activeOperations++;
            return true;
        }
    }

    private void ExitOperation()
    {
        lock (gate)
        {
            activeOperations--;
            if (stopping && activeOperations == 0)
            {
                drained?.TrySetResult();
            }
        }
    }

    private async Task MarkUnavailableAsync(string serverName)
    {
        state.MarkStatus(serverName, ExternalMcpServerStatus.Unavailable);
        var connection = state.RemoveConnection(serverName);
        if (connection is not null)
        {
            await connection.DisposeSafelyAsync(logger);
        }
    }

    private async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "External MCP background task failed; exception type {ExceptionType}.",
                exception.GetType().Name);
        }
    }
}
