using System.Security.Cryptography;
using System.Text.Json;

namespace GenAIPlatform.Infrastructure.Mcp;

internal static class ExternalMcpSnapshotHasher
{
    public static string Hash(ExternalMcpToolSnapshot tool)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            server = tool.ServerName,
            originalName = tool.OriginalName,
            prefixedName = tool.PrefixedName,
            description = tool.Description,
            inputSchema = tool.InputSchema,
            schemaless = tool.IsSchemaless
        });

        return $"sha256:{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}";
    }
}
