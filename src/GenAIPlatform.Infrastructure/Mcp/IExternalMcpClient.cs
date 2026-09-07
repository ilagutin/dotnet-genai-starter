namespace GenAIPlatform.Infrastructure.Mcp;

internal interface IExternalMcpClient : IAsyncDisposable
{
    Task<IReadOnlyList<ExternalMcpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken);

    Task<ExternalMcpToolCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken);
}
