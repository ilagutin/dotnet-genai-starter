namespace GenAIPlatform.Infrastructure.Mcp;

// Marks caller cancellation after dispatch without retaining a provider exception or payload.
internal sealed class ExternalMcpCallCanceledException(CancellationToken cancellationToken)
    : OperationCanceledException("External MCP tool outcome is unknown after dispatch.", cancellationToken);
