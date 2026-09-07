namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed class ExternalMcpConnectionState
{
    private readonly object gate = new();
    private readonly Dictionary<string, ExternalMcpServerConnection> connections = new(StringComparer.Ordinal);
    private IReadOnlyList<ExternalMcpServerSnapshot> snapshots = [];

    public IReadOnlyList<ExternalMcpServerSnapshot> GetSnapshots()
    {
        lock (gate)
        {
            return snapshots;
        }
    }

    public void UpsertSnapshot(ExternalMcpServerSnapshot snapshot)
    {
        lock (gate)
        {
            // Replace this server's snapshot only, leaving concurrent changes to other servers
            // intact. Order by the configured index so the listing stays deterministic.
            snapshots = snapshots
                .Where(existing => !string.Equals(existing.ServerName, snapshot.ServerName, StringComparison.Ordinal))
                .Append(snapshot)
                .OrderBy(static existing => existing.Order)
                .ToArray();
        }
    }

    public void SetConnection(string serverName, ExternalMcpServerConnection connection)
    {
        lock (gate)
        {
            connections[serverName] = connection;
        }
    }

    public bool TryGetConnection(string serverName, out ExternalMcpServerConnection? connection)
    {
        lock (gate)
        {
            return connections.TryGetValue(serverName, out connection);
        }
    }

    public ExternalMcpServerConnection? RemoveConnection(string serverName)
    {
        lock (gate)
        {
            if (!connections.Remove(serverName, out var connection))
            {
                return null;
            }

            return connection;
        }
    }

    public void MarkStatus(string serverName, ExternalMcpServerStatus status)
    {
        lock (gate)
        {
            snapshots = snapshots
                .Select(snapshot => string.Equals(snapshot.ServerName, serverName, StringComparison.Ordinal)
                    ? snapshot with { Status = status }
                    : snapshot)
                .ToArray();
        }
    }

    public IReadOnlyList<ExternalMcpServerConnection> ClearConnections()
    {
        lock (gate)
        {
            var current = connections.Values.ToArray();
            connections.Clear();
            snapshots = snapshots
                .Select(static snapshot => snapshot with { Status = ExternalMcpServerStatus.Unavailable })
                .ToArray();
            return current;
        }
    }
}
