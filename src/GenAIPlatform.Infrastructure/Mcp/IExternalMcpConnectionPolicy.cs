namespace GenAIPlatform.Infrastructure.Mcp;

/// <summary>
/// Decides whether a connect attempt to an external MCP server may proceed, and records
/// connect outcomes. The default <see cref="AlwaysConnectMcpPolicy"/> always allows attempts
/// and ignores outcomes. This seam lets a later release plug in circuit-breaker / backoff
/// behavior for graceful degradation without touching the connection manager or its callers. Only
/// transport/connect outcomes flow through here; logical tool errors are not connection failures
/// and must not be recorded.
/// </summary>
internal interface IExternalMcpConnectionPolicy
{
    bool ShouldAttemptConnect(string serverName);

    void RecordConnectSuccess(string serverName);

    void RecordConnectFailure(string serverName);
}
