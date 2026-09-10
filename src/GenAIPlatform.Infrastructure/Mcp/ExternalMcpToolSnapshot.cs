using System.Text.Json;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed record ExternalMcpToolSnapshot(
    string ServerName,
    string OriginalName,
    string PrefixedName,
    string Description,
    string SnapshotHash,
    JsonElement InputSchema,
    bool IsSchemaless,
    TimeSpan ToolCallTimeout);
