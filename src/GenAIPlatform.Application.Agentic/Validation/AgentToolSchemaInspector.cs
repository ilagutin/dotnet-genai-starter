using System.Globalization;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace GenAIPlatform.Application.Agentic.Validation;

internal static class AgentToolSchemaInspector
{
    private static readonly HashSet<string> SingleSchemaKeywords = new(StringComparer.Ordinal)
    {
        "additionalProperties",
        "unevaluatedProperties",
        "propertyNames",
        "contains",
        "items",
        "unevaluatedItems",
        "not",
        "if",
        "then",
        "else",
        "contentSchema"
    };

    private static readonly HashSet<string> SchemaArrayKeywords = new(StringComparer.Ordinal)
    {
        "allOf",
        "anyOf",
        "oneOf",
        "prefixItems"
    };

    private static readonly HashSet<string> SchemaMapKeywords = new(StringComparer.Ordinal)
    {
        "properties",
        "patternProperties",
        "dependentSchemas",
        "$defs"
    };

    public static string? InspectStructure(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(schema.GetRawText()) > AgentToolArgumentValidator.MaxUtf8Bytes)
        {
            return "/";
        }

        var nodes = 0;
        return InspectStructure(schema, 0, string.Empty, ref nodes);
    }

    public static string? InspectKeywords(JsonSchema schema)
    {
        var visited = new HashSet<JsonSchemaNode>(ReferenceEqualityComparer.Instance);
        return InspectKeywords(schema.Root, visited);
    }

    private static string? InspectStructure(JsonElement value, int depth, string path, ref int nodes)
    {
        nodes++;
        if (depth > AgentToolArgumentValidator.MaxJsonDepth ||
            nodes > AgentToolArgumentValidator.MaxSchemaNodes)
        {
            return path;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var propertyPath = AppendPath(path, property.Name);
                var failure = InspectStructure(property.Value, depth + 1, propertyPath, ref nodes);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var itemPath = AppendPath(path, index.ToString(CultureInfo.InvariantCulture));
                var failure = InspectStructure(item, depth + 1, itemPath, ref nodes);
                if (failure is not null)
                {
                    return failure;
                }

                index++;
            }
        }

        return null;
    }

    private static string? InspectKeywords(JsonSchemaNode node, HashSet<JsonSchemaNode> visited)
    {
        if (!visited.Add(node))
        {
            return null;
        }

        var failure = InspectSchemaValue(node.Source);
        if (failure is not null)
        {
            return failure;
        }

        foreach (var keyword in node.Keywords)
        {
            foreach (var subschema in keyword.Subschemas)
            {
                failure = InspectKeywords(subschema, visited);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        return null;
    }

    private static string? InspectSchemaValue(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.True || schema.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "/";
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name is "pattern" or "patternProperties" or "$recursiveRef")
            {
                return "/";
            }

            if (property.Name is "$ref" or "$dynamicRef")
            {
                var reference = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
                if (reference is null || !reference.StartsWith('#'))
                {
                    return "/";
                }
            }

            if (property.Name == "$schema" &&
                (property.Value.ValueKind != JsonValueKind.String ||
                 property.Value.GetString() != "https://json-schema.org/draft/2020-12/schema"))
            {
                return "/";
            }

            if (SingleSchemaKeywords.Contains(property.Name))
            {
                var failure = InspectSchemaValue(property.Value);
                if (failure is not null)
                {
                    return failure;
                }
            }
            else if (SchemaArrayKeywords.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var subschema in property.Value.EnumerateArray())
                {
                    var failure = InspectSchemaValue(subschema);
                    if (failure is not null)
                    {
                        return failure;
                    }
                }
            }
            else if (SchemaMapKeywords.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var namedSchema in property.Value.EnumerateObject())
                {
                    var failure = InspectSchemaValue(namedSchema.Value);
                    if (failure is not null)
                    {
                        return failure;
                    }
                }
            }
        }

        return null;
    }

    private static string AppendPath(string path, string segment)
    {
        return $"{path}/{segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";
    }
}
