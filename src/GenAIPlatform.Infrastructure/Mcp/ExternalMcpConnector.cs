using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Mcp;

/// <summary>Connects and snapshots configured external MCP servers.</summary>
internal sealed class ExternalMcpConnector(
    IOptions<ExternalMcpOptions> options,
    IExternalMcpClientFactory clientFactory,
    IExternalMcpConnectionPolicy policy,
    ILogger logger)
{
    public async Task RefreshAsync(
        ExternalMcpConnectionState state,
        CancellationToken cancellationToken)
    {
        var servers = EnabledServers().ToArray();
        var current = state.GetSnapshots().ToDictionary(static snapshot => snapshot.ServerName, StringComparer.Ordinal);
        var maxParallel = Math.Max(1, options.Value.MaxParallelConnects);
        using var slots = new SemaphoreSlim(maxParallel, maxParallel);

        var attempts = new List<Task>(servers.Length);
        for (var index = 0; index < servers.Length; index++)
        {
            var server = servers[index];
            var serverName = ExternalMcpNameSanitizer.SanitizeSegment(server.Name, "server");

            // Leave a working server untouched: no reconnect, no re-list, stable snapshot hash.
            if (current.TryGetValue(serverName, out var existing) && existing.IsAvailable)
            {
                continue;
            }

            attempts.Add(ConnectAndUpsertAsync(slots, state, server, index, cancellationToken));
        }

        await Task.WhenAll(attempts);
    }

    private async Task ConnectAndUpsertAsync(
        SemaphoreSlim slots,
        ExternalMcpConnectionState state,
        ExternalMcpServerOptions server,
        int order,
        CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken);
        try
        {
            // Order is the configured index, so bounded-parallel attempts never change listing order.
            var snapshot = await ConnectAndSnapshotAsync(state, server, order, cancellationToken);
            state.UpsertSnapshot(snapshot);
        }
        finally
        {
            slots.Release();
        }
    }

    public async Task<ExternalMcpServerConnection?> GetOrReconnectAsync(
        ExternalMcpConnectionState state,
        string serverName,
        CancellationToken cancellationToken)
    {
        if (state.TryGetConnection(serverName, out var connection))
        {
            return connection;
        }

        var server = FindServer(serverName);
        if (server is null)
        {
            return null;
        }

        if (!policy.ShouldAttemptConnect(serverName))
        {
            state.MarkStatus(serverName, ExternalMcpServerStatus.Paused);
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(server.ConnectTimeoutSeconds));
            var client = await clientFactory.CreateAsync(server, timeout.Token);
            connection = new ExternalMcpServerConnection(server, client);
            if (!state.TrySetConnection(serverName, connection))
            {
                await connection.DisposeSafelyAsync(logger);
                return state.TryGetConnection(serverName, out var existing)
                    ? existing
                    : null;
            }

            state.MarkStatus(serverName, ExternalMcpServerStatus.Available);
            policy.RecordConnectSuccess(serverName);
            return connection;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            policy.RecordConnectFailure(serverName);
            LogFailure("reconnect failed", serverName, exception);
            state.MarkStatus(serverName, ExternalMcpServerStatus.Unavailable);
            return null;
        }
    }

    private async Task<ExternalMcpServerSnapshot> ConnectAndSnapshotAsync(
        ExternalMcpConnectionState state,
        ExternalMcpServerOptions server,
        int order,
        CancellationToken cancellationToken)
    {
        var serverName = ExternalMcpNameSanitizer.SanitizeSegment(server.Name, "server");
        if (!policy.ShouldAttemptConnect(serverName))
        {
            return new ExternalMcpServerSnapshot(serverName, order, ExternalMcpServerStatus.Paused, []);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(server.ConnectTimeoutSeconds));

        IExternalMcpClient? client = null;
        try
        {
            client = await clientFactory.CreateAsync(server, timeout.Token);
            var descriptors = await client.ListToolsAsync(timeout.Token);
            var snapshot = ExternalMcpSnapshotBuilder.Build(server, order, descriptors, ExternalMcpServerStatus.Available);
            var connection = new ExternalMcpServerConnection(server, client);
            client = null;
            if (!state.TrySetConnection(snapshot.ServerName, connection))
            {
                await connection.DisposeSafelyAsync(logger);
                return state.TryGetConnection(snapshot.ServerName, out _)
                    ? snapshot
                    : snapshot with { Status = ExternalMcpServerStatus.Unavailable, Tools = [] };
            }

            policy.RecordConnectSuccess(serverName);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (client is not null)
            {
                await DisposeDetachedClientAsync(client, serverName);
            }

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (client is not null)
            {
                await DisposeDetachedClientAsync(client, serverName);
            }

            policy.RecordConnectFailure(serverName);
            LogFailure("initial connection failed", serverName, exception);
            return new ExternalMcpServerSnapshot(serverName, order, ExternalMcpServerStatus.Unavailable, []);
        }
    }

    private ExternalMcpServerOptions? FindServer(string serverName)
    {
        return EnabledServers().FirstOrDefault(candidate =>
            string.Equals(
                ExternalMcpNameSanitizer.SanitizeSegment(candidate.Name, "server"),
                serverName,
                StringComparison.Ordinal));
    }

    private IEnumerable<ExternalMcpServerOptions> EnabledServers() =>
        options.Value.Servers.Where(static server => server.Enabled);

    private void LogFailure(string action, string serverName, Exception exception)
    {
        logger.LogWarning(
            "External MCP {Action} for server {ServerName}; exception type {ExceptionType}.",
            action,
            ExternalMcpNameSanitizer.SanitizeLogIdentity(serverName),
            exception.GetType().Name);
    }

    private async Task DisposeDetachedClientAsync(IExternalMcpClient client, string serverName)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception exception)
        {
            LogFailure("detached client dispose failed", serverName, exception);
        }
    }
}
