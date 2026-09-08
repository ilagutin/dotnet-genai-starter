namespace GenAIPlatform.Infrastructure.Mcp;

using Microsoft.Extensions.Logging;

internal sealed class ExternalMcpServerConnection(
    ExternalMcpServerOptions options,
    IExternalMcpClient client) : IAsyncDisposable
{
    private readonly object gate = new();
    private Task? disposal;

    public ExternalMcpServerOptions Options { get; } = options;

    public IExternalMcpClient Client { get; } = client;

    public async Task<(ExternalMcpToolCallResult Result, bool MarkUnavailable)> TryCallAsync(
        ExternalMcpToolSnapshot tool,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken operationToken,
        CancellationToken callerToken,
        CancellationToken shutdownToken,
        ILogger logger)
    {
        callerToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        timeout.CancelAfter(tool.ToolCallTimeout);
        callerToken.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested || shutdownToken.IsCancellationRequested)
        {
            return (ExternalMcpToolCallResult.Unavailable("External MCP tool dispatch was canceled."), false);
        }

        try
        {
            return (await Client.CallToolAsync(tool.OriginalName, arguments, timeout.Token), false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw new ExternalMcpCallCanceledException(callerToken);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            return (ExternalMcpToolCallResult.OutcomeUnknown(), true);
        }
        catch (OperationCanceledException exception)
        {
            LogFailure(logger, "timed out", tool.ServerName, exception);
            return (ExternalMcpToolCallResult.OutcomeUnknown(), true);
        }
        catch (Exception exception)
        {
            LogFailure(logger, "call failure", tool.ServerName, exception);
            return (ExternalMcpToolCallResult.OutcomeUnknown(), true);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            return new ValueTask(disposal ??= DisposeCoreAsync());
        }
    }

    public async Task DisposeSafelyAsync(ILogger logger)
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception exception)
        {
            LogFailure(logger, "dispose failed", Options.Name, exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Client.DisposeAsync();
    }

    private static void LogFailure(ILogger logger, string action, string serverName, Exception exception)
    {
        logger.LogWarning(
            "External MCP {Action} for server {ServerName}; exception type {ExceptionType}.",
            action,
            ExternalMcpNameSanitizer.SanitizeLogIdentity(serverName),
            exception.GetType().Name);
    }
}
