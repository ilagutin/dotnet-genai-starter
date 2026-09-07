using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed class SdkExternalMcpClientFactory(ILoggerFactory loggerFactory) : IExternalMcpClientFactory
{
    public async Task<IExternalMcpClient> CreateAsync(
        ExternalMcpServerOptions server,
        CancellationToken cancellationToken)
    {
        var transportOptions = new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command,
            Arguments = server.Arguments,
            WorkingDirectory = server.WorkingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables()
        };
        var transport = new StdioClientTransport(transportOptions, loggerFactory);
        var client = await McpClient.CreateAsync(
            transport,
            clientOptions: null,
            loggerFactory,
            cancellationToken);

        return new SdkExternalMcpClient(client);
    }
}
