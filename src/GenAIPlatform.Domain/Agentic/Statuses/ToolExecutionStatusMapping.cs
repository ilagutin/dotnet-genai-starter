namespace GenAIPlatform.Domain.Agentic;

public static class ToolExecutionStatusMapping
{
    public static string ToPublicValue(this ToolExecutionStatus status) => status switch
    {
        ToolExecutionStatus.NotExecuted => "NotExecuted",
        ToolExecutionStatus.Rejected => "Rejected",
        ToolExecutionStatus.ValidationFailed => "ValidationFailed",
        ToolExecutionStatus.ApprovalRequired => "ApprovalRequired",
        ToolExecutionStatus.Failed => "Failed",
        ToolExecutionStatus.Succeeded => "Succeeded",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Undefined ToolExecutionStatus value.")
    };
}
