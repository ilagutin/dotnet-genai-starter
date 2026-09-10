namespace GenAIPlatform.Domain.Agentic;

public static class ToolValidationStatusMapping
{
    public static string ToPublicValue(this ToolValidationStatus status) => status switch
    {
        ToolValidationStatus.Valid => "Valid",
        ToolValidationStatus.Invalid => "Invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Undefined ToolValidationStatus value.")
    };
}
