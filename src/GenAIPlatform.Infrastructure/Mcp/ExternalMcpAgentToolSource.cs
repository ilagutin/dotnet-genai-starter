using GenAIPlatform.Application.Agentic.Tools;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed class ExternalMcpAgentToolSource(
    IExternalMcpConnectionManager connectionManager,
    ILoggerFactory loggerFactory)
    : IExternalAgentToolSource
{
    public IReadOnlyList<IAgentTool> GetAvailableTools()
    {
        return connectionManager
            .GetSnapshots()
            .Where(static snapshot => snapshot.IsAvailable)
            .OrderBy(static snapshot => snapshot.Order)
            .SelectMany(static snapshot => snapshot.Tools.OrderBy(
                static tool => tool.PrefixedName,
                StringComparer.Ordinal))
            .Select(tool => new ExternalMcpAgentTool(
                connectionManager, tool, loggerFactory.CreateLogger<ExternalMcpAgentTool>()))
            .ToArray();
    }
}
