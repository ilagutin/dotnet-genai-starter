using System.Text.Json;
using ModelContextProtocol.Client;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed class SdkExternalMcpClient(McpClient client) : IExternalMcpClient
{
    public async Task<IReadOnlyList<ExternalMcpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools
            .Select(static tool => new ExternalMcpToolDescriptor(
                tool.ProtocolTool.Name,
                tool.ProtocolTool.Description,
                ExternalMcpJsonRoundTrip.CloneSchema(tool.ProtocolTool.InputSchema)))
            .ToArray();
    }

    public async Task<ExternalMcpToolCallResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(
            toolName,
            arguments,
            cancellationToken: cancellationToken);
        var payload = JsonSerializer.SerializeToElement(result);

        return new ExternalMcpToolCallResult(
            result.IsError == true,
            payload,
            result.IsError == true ? "External MCP tool returned an error." : null);
    }

    public async ValueTask DisposeAsync()
    {
        if (client is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
