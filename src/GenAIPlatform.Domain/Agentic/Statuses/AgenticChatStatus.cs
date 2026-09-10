namespace GenAIPlatform.Domain.Agentic;

public enum AgenticChatStatus
{
    Succeeded,
    TimedOut,
    BudgetExceeded,
    ToolLimitExceeded,
    ToolFailed,
    ToolRejected,
    ApprovalRequired,
    StepLimitExceeded,
    UsageUnavailable,
    InvalidUsage
}
