using System.Globalization;
using System.Text;
using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools;
using Json.Schema;

namespace GenAIPlatform.Application.Agentic.Validation;

public sealed class AgentToolArgumentValidator
{
    internal const int MaxUtf8Bytes = 64 * 1024;
    internal const int MaxJsonDepth = 32;
    internal const int MaxSchemaNodes = 256;
    internal const int MaxErrorLength = 256;
    internal const string SchemaInvalidCode = "schema_invalid";
    internal const string SchemaDefinitionInvalidCode = "schema_definition_invalid";

    public ToolValidationResult Validate(IAgentTool tool, JsonElement arguments)
    {
        var schemaPath = AgentToolSchemaInspector.InspectStructure(tool.Definition.InputSchema);
        if (schemaPath is not null)
        {
            return InvalidDefinition(schemaPath);
        }

        var argumentPath = InspectJson(arguments);
        if (argumentPath is not null)
        {
            return InvalidPayload("/");
        }

        try
        {
            var registry = new SchemaRegistry();
            var schema = JsonSchema.FromText(
                tool.Definition.InputSchema.GetRawText(),
                new BuildOptions
                {
                    Dialect = Dialect.Draft202012,
                    SchemaRegistry = registry,
                    DialectRegistry = new DialectRegistry(),
                    VocabularyRegistry = new VocabularyRegistry()
                });
            schemaPath = AgentToolSchemaInspector.InspectKeywords(schema);
            if (schemaPath is not null)
            {
                return InvalidDefinition(schemaPath);
            }

            var results = schema.Evaluate(
                arguments,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!results.IsValid)
            {
                return InvalidPayload(FirstFailurePath(results));
            }
        }
        catch (Exception exception) when (exception is
            JsonException or JsonSchemaException or ArgumentException or FormatException or
            InvalidOperationException or NotSupportedException)
        {
            return InvalidDefinition("/");
        }

        return tool.Validate(arguments);
    }

    private static string? InspectJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined ||
            Encoding.UTF8.GetByteCount(value.GetRawText()) > MaxUtf8Bytes)
        {
            return "/";
        }

        return InspectNode(value, 0, string.Empty);
    }

    private static string? InspectNode(
        JsonElement value,
        int depth,
        string currentPath)
    {
        if (depth > MaxJsonDepth)
        {
            return currentPath;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var propertyPath = AppendPath(currentPath, property.Name);
                var nested = InspectNode(property.Value, depth + 1, propertyPath);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var itemPath = AppendPath(currentPath, index.ToString(CultureInfo.InvariantCulture));
                var nested = InspectNode(item, depth + 1, itemPath);
                if (nested is not null)
                {
                    return nested;
                }

                index++;
            }
        }

        return null;
    }

    private static string FirstFailurePath(EvaluationResults results)
    {
        var paths = Flatten(results)
            .Where(static result => !result.IsValid)
            .Select(static result => result.EvaluationPath.ToString())
            .Where(static path => !string.IsNullOrEmpty(path))
            .Order(StringComparer.Ordinal);
        return paths.FirstOrDefault() ?? "/";
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults result)
    {
        yield return result;
        foreach (var detail in result.Details ?? [])
        {
            foreach (var nested in Flatten(detail))
            {
                yield return nested;
            }
        }
    }

    private static ToolValidationResult InvalidPayload(string path)
    {
        return ToolValidationResult.Invalid(
            SchemaInvalidCode,
            SafeMessage("Tool arguments do not match the declared schema at ", path));
    }

    private static ToolValidationResult InvalidDefinition(string path)
    {
        return ToolValidationResult.Invalid(
            SchemaDefinitionInvalidCode,
            SafeMessage("Tool schema is invalid or unsupported at ", path));
    }

    private static string SafeMessage(string prefix, string path)
    {
        var printable = new string(path.Select(static character =>
            char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or
                UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned
                ? '?'
                : character).ToArray());
        var message = prefix + (string.IsNullOrWhiteSpace(printable) ? "/" : printable);
        return message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
    }

    private static string AppendPath(string path, string segment)
    {
        return $"{path}/{segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";
    }
}
