using System.Text.Json;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Tools.Execution;

internal sealed record AgentToolExecutionResult(
    string ToolCallId,
    string ToolName,
    string ResponseSchemaVersion,
    string AuditSchemaVersion,
    ToolValidationResult Validation,
    ToolPolicyDecision Policy,
    ToolApprovalState ApprovalState,
    ToolExecutionStatus ExecutionStatus,
    JsonElement? Output,
    string? ErrorCode,
    string? ErrorMessage,
    Exception? Exception = null)
{
    public string? ResultText => Output?.GetRawText();
}
