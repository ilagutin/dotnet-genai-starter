using System.Text.Json;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed record ExternalMcpToolDescriptor(
    string Name,
    string? Description,
    JsonElement? InputSchema);
