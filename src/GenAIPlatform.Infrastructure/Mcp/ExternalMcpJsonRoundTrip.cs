using System.Text.Json;

namespace GenAIPlatform.Infrastructure.Mcp;

internal static class ExternalMcpJsonRoundTrip
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyDictionary<string, object?>? ToSdkArguments(JsonElement arguments)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        // Deserializing to object? leaves every nested value as a JsonElement. This is intentional:
        // the MCP SDK serializes these arguments back to JSON-RPC via System.Text.Json, which writes
        // each JsonElement as its raw token, so nested objects/arrays and full numeric precision (big
        // integers, high-precision decimals) survive without a lossy box to double/long. The coupling
        // is to System.Text.Json's behavior, not to a specific SDK shape; a hand-rolled
        // JsonElement->CLR mapper would reintroduce numeric-fidelity risk. Pinned by
        // ToSdkArguments_PreservesNestedShapeAndNumericFidelity.
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(
            arguments.GetRawText(),
            SerializerOptions) ?? new Dictionary<string, object?>();
    }

    public static JsonElement CloneObjectSchema(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<JsonElement>(schema.GetRawText(), SerializerOptions).Clone();
        }

        return JsonSerializer.SerializeToElement(new { type = "object" }, SerializerOptions);
    }

    public static JsonElement EmptyObject()
    {
        return JsonSerializer.SerializeToElement(new { }, SerializerOptions);
    }
}
