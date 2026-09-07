using System.Security.Cryptography;
using System.Text.Json;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Tools;

internal sealed class CreateSupportTicketTool : IAgentTool
{
    public AiToolDefinition Definition { get; } = new(
        "CreateSupportTicket",
        "Creates an idempotent demo support ticket.",
        "v1",
        Json("""
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "description": { "type": "string" },
            "priority": { "type": "string", "enum": [ "low", "normal", "high" ] }
          },
          "required": [ "title", "description" ],
          "additionalProperties": false
        }
        """));

    public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
        "Creates an idempotent demo support ticket record.");

    public ToolValidationResult Validate(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return ToolValidationResult.Invalid("invalid_arguments", "CreateSupportTicket expects an object argument.");
        }

        var title = ReadRequiredString(arguments, "title");
        var description = ReadRequiredString(arguments, "description");
        if (title is null || description is null)
        {
            return ToolValidationResult.Invalid("missing_required_argument", "title and description are required.");
        }

        var priority = ReadOptionalString(arguments, "priority") ?? "normal";
        if (priority is not ("low" or "normal" or "high"))
        {
            return ToolValidationResult.Invalid("invalid_priority", "priority must be low, normal or high.");
        }

        return ToolValidationResult.Valid(JsonSerializer.SerializeToElement(new
        {
            title,
            description,
            priority
        }));
    }

    public Task<ToolExecutionResult> ExecuteAsync(
        JsonElement sanitizedArguments,
        CancellationToken cancellationToken)
    {
        // Demo tool: returns a synthetic ticket id; no external ticketing system is called.
        var stableInput = sanitizedArguments.GetRawText();
        var ticketHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stableInput));
        var ticketNumber = BitConverter.ToUInt32(ticketHash, 0) % 100000;
        var ticketId = $"SUP-{ticketNumber:00000}";
        var output = JsonSerializer.SerializeToElement(new
        {
            ticketId,
            status = "Created",
            demoOnly = true
        });

        return Task.FromResult(new ToolExecutionResult(ToolExecutionStatus.Succeeded, output));
    }

    private static string? ReadRequiredString(JsonElement arguments, string propertyName)
    {
        return ReadOptionalString(arguments, propertyName);
    }

    private static string? ReadOptionalString(JsonElement arguments, string propertyName)
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
