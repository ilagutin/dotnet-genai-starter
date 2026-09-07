using System.Text.Json;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Tools;

internal sealed class DraftEmailTool : IAgentTool
{
    public AiToolDefinition Definition { get; } = new(
        "DraftEmail",
        "Creates a local draft email payload. It never sends email.",
        "v1",
        Json("""
        {
          "type": "object",
          "properties": {
            "to": { "type": "string" },
            "subject": { "type": "string" },
            "body": { "type": "string" }
          },
          "required": [ "to", "subject", "body" ],
          "additionalProperties": false
        }
        """));

    public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.ApprovalRequired(
        "Email content is generated as a draft only and requires simulated approval.");

    public ToolValidationResult Validate(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return ToolValidationResult.Invalid("invalid_arguments", "DraftEmail expects an object argument.");
        }

        var to = ReadRequiredString(arguments, "to");
        var subject = ReadRequiredString(arguments, "subject");
        var body = ReadRequiredString(arguments, "body");
        if (to is null || subject is null || body is null)
        {
            return ToolValidationResult.Invalid("missing_required_argument", "to, subject and body are required.");
        }

        return ToolValidationResult.Valid(JsonSerializer.SerializeToElement(new
        {
            to,
            subject,
            body,
            mode = "draft"
        }));
    }

    public Task<ToolExecutionResult> ExecuteAsync(
        JsonElement sanitizedArguments,
        CancellationToken cancellationToken)
    {
        // Demo tool: returns a synthetic draft id; no email leaves the process.
        var draftId = $"DRAFT-{Guid.NewGuid():N}";
        var output = JsonSerializer.SerializeToElement(new
        {
            draftId,
            status = "DraftCreated",
            sent = false
        });

        return Task.FromResult(new ToolExecutionResult(ToolExecutionStatus.Succeeded, output));
    }

    private static string? ReadRequiredString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
