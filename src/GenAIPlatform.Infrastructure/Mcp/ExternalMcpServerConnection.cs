namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed record ExternalMcpServerConnection(
    ExternalMcpServerOptions Options,
    IExternalMcpClient Client) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        return Client.DisposeAsync();
    }
}
