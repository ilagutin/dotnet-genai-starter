using System.Text.Json;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Validation;

public sealed record ToolValidationResult(
    ToolValidationStatus Status,
    JsonElement SanitizedArguments,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsValid => Status == ToolValidationStatus.Valid;

    public static ToolValidationResult Valid(JsonElement sanitizedArguments)
    {
        return new ToolValidationResult(ToolValidationStatus.Valid, sanitizedArguments, null, null);
    }

    public static ToolValidationResult Invalid(string errorCode, string errorMessage)
    {
        return new ToolValidationResult(ToolValidationStatus.Invalid, EmptyJsonObject(), errorCode, errorMessage);
    }

    private static JsonElement EmptyJsonObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
