namespace GenAIPlatform.Domain.Agentic;

public static class AgenticChatStatusMapping
{
    public static string ToPublicValue(this AgenticChatStatus status) => status switch
    {
        AgenticChatStatus.Succeeded => "Succeeded",
        AgenticChatStatus.TimedOut => "TimedOut",
        AgenticChatStatus.BudgetExceeded => "BudgetExceeded",
        AgenticChatStatus.ToolLimitExceeded => "ToolLimitExceeded",
        AgenticChatStatus.ToolFailed => "ToolFailed",
        AgenticChatStatus.ToolRejected => "ToolRejected",
        AgenticChatStatus.ApprovalRequired => "ApprovalRequired",
        AgenticChatStatus.StepLimitExceeded => "StepLimitExceeded",
        AgenticChatStatus.UsageUnavailable => "UsageUnavailable",
        AgenticChatStatus.InvalidUsage => "InvalidUsage",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Undefined AgenticChatStatus value.")
    };
}
