using System.Text.Json;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Tools.Execution;

internal static class AgentToolAuditProjection
{
    public static ToolAuditLogEntry Create(
        AgentToolExecutionResult result,
        AgentToolExecutionContext context,
        DateTimeOffset createdAt)
    {
        var metadataOnly = result.AuditContentPolicy == ToolAuditContentPolicy.MetadataOnly;
        return new ToolAuditLogEntry(
            Guid.NewGuid(),
            context.ConversationId,
            context.TenantId,
            context.UserId,
            context.CorrelationId,
            result.ToolCallId,
            result.ToolName,
            result.AuditSchemaVersion,
            context.PolicyVersion,
            result.Validation.Status.ToPublicValue(),
            result.Policy.Decision,
            result.ApprovalState.ToPublicValue(),
            result.ExecutionStatus.ToPublicValue(),
            metadataOnly ? ArgumentMetadata(result.ArgumentUtf8Bytes) : result.Validation.SanitizedArguments,
            metadataOnly ? OutputMetadata(result) : result.Output,
            result.ErrorCode,
            metadataOnly ? null : result.ErrorMessage,
            createdAt);
    }

    private static JsonElement ArgumentMetadata(int utf8Bytes)
    {
        return JsonSerializer.SerializeToElement(new
        {
            contentOmitted = true,
            utf8Bytes
        });
    }

    private static JsonElement? OutputMetadata(AgentToolExecutionResult result)
    {
        if (result.PayloadMetadata is not { } metadata)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(new
        {
            contentOmitted = true,
            sourceUtf8Bytes = metadata.SourceUtf8Bytes,
            returnedUtf8Bytes = metadata.ReturnedUtf8Bytes,
            truncated = metadata.Truncated
        });
    }
}
