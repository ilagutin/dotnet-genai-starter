namespace GenAIPlatform.Infrastructure.Mcp;

internal interface IExternalMcpClientFactory
{
    Task<IExternalMcpClient> CreateAsync(
        ExternalMcpServerOptions server,
        CancellationToken cancellationToken);
}
