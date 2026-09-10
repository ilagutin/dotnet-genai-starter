using System.Text;
using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools;
using ModelContextProtocol.Protocol;

namespace GenAIPlatform.Infrastructure.Mcp;

internal static class ExternalMcpToolResultMapper
{
    public const int MinimumResultBytes = 23;

    public static ExternalMcpToolCallResult Map(CallToolResult result, int maxBytes)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            content = result.Content.Select(WithoutProtocolMetadata),
            structuredContent = result.StructuredContent?.Clone()
        });
        var sourceBytes = Utf8Bytes(payload);
        if (sourceBytes <= maxBytes)
        {
            return Create(result, payload, new ToolExecutionPayloadMetadata(sourceBytes, sourceBytes, false));
        }

        var omitted = JsonSerializer.SerializeToElement(new { contentOmitted = true });
        var returnedBytes = Utf8Bytes(omitted);
        return Create(result, omitted, new ToolExecutionPayloadMetadata(sourceBytes, returnedBytes, true));
    }

    private static ExternalMcpToolCallResult Create(
        CallToolResult result,
        JsonElement payload,
        ToolExecutionPayloadMetadata metadata)
    {
        var isError = result.IsError == true;
        return new ExternalMcpToolCallResult(
            isError,
            payload,
            isError ? "External MCP tool returned an error." : null,
            PayloadMetadata: metadata);
    }

    private static JsonElement WithoutProtocolMetadata(ContentBlock content)
    {
        var value = JsonSerializer.SerializeToElement(content);
        if (value.ValueKind != JsonValueKind.Object)
        {
            return value;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "_meta" or "meta")
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    private static int Utf8Bytes(JsonElement value)
    {
        return Encoding.UTF8.GetByteCount(value.GetRawText());
    }
}
