using System.Text.Json;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Tools.Execution;

internal sealed record AgentToolExecutionResult(
    string ToolCallId,
    string ToolName,
    string ResponseSchemaVersion,
    string AuditSchemaVersion,
    ToolAuditContentPolicy AuditContentPolicy,
    int ArgumentUtf8Bytes,
    ToolValidationResult Validation,
    ToolPolicyDecision Policy,
    ToolApprovalState ApprovalState,
    ToolExecutionStatus ExecutionStatus,
    JsonElement? Output,
    ToolExecutionPayloadMetadata? PayloadMetadata,
    string? ErrorCode,
    string? ErrorMessage,
    Exception? Exception = null)
{
    public string? ResultText => Output?.GetRawText();
}
