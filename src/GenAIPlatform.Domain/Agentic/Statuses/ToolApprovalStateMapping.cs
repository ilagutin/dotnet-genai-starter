namespace GenAIPlatform.Domain.Agentic;

public static class ToolApprovalStateMapping
{
    public static string ToPublicValue(this ToolApprovalState status) => status switch
    {
        ToolApprovalState.NotRequired => "NotRequired",
        ToolApprovalState.Required => "Required",
        ToolApprovalState.SimulatedApproved => "SimulatedApproved",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Undefined ToolApprovalState value.")
    };
}
