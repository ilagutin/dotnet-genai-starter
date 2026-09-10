using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed class SdkExternalMcpClientFactory(IOptions<ExternalMcpOptions> options) : IExternalMcpClientFactory
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
        var transport = new StdioClientTransport(transportOptions, NullLoggerFactory.Instance);
        var client = await McpClient.CreateAsync(
            transport,
            clientOptions: null,
            NullLoggerFactory.Instance,
            cancellationToken);

        return new SdkExternalMcpClient(client, options.Value.MaxToolResultBytes);
    }
}
