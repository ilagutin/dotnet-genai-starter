namespace GenAIPlatform.Infrastructure.Mcp;

internal interface IExternalMcpConnectionManager
{
    IReadOnlyList<ExternalMcpServerSnapshot> GetSnapshots();

    /// <summary>
    /// Runs one connection pass: attempts servers that are not currently available (policy-gated,
    /// bounded-parallel) and lists their tools, leaving already-available servers untouched. Used
    /// by the background recovery loop and by on-demand consumers that connect per workflow stage.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken);

    Task<ExternalMcpToolCallResult> CallToolAsync(
        ExternalMcpToolSnapshot tool,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken);
}
