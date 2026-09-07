using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Infrastructure.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace GenAIPlatform.Infrastructure.Agentic;

internal sealed class PostgresToolAuditLogRepository(PostgresDataSourceProvider dataSourceProvider)
    : IToolAuditLogRepository
{
    public async Task AddAsync(
        ToolAuditLogEntry entry,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.tool_audit_logs (
                id, conversation_id, tenant_id, user_id, correlation_id, tool_call_id,
                tool_name, schema_version, policy_version, validation_status, policy_decision,
                approval_state, execution_status, arguments, output, error_code,
                error_message, created_at_utc)
            VALUES (
                @id, @conversation_id, @tenant_id, @user_id, @correlation_id, @tool_call_id,
                @tool_name, @schema_version, @policy_version, @validation_status, @policy_decision,
                @approval_state, @execution_status, @arguments, @output, @error_code,
                @error_message, @created_at_utc);
            """, connection);

        AddParameter(command, "id", entry.Id);
        AddParameter(command, "conversation_id", entry.ConversationId);
        AddParameter(command, "tenant_id", entry.TenantId);
        AddParameter(command, "user_id", entry.UserId);
        AddParameter(command, "correlation_id", entry.CorrelationId);
        AddParameter(command, "tool_call_id", entry.ToolCallId);
        AddParameter(command, "tool_name", entry.ToolName);
        AddParameter(command, "schema_version", entry.SchemaVersion);
        AddParameter(command, "policy_version", entry.PolicyVersion);
        AddParameter(command, "validation_status", entry.ValidationStatus);
        AddParameter(command, "policy_decision", entry.PolicyDecision);
        AddParameter(command, "approval_state", entry.ApprovalState);
        AddParameter(command, "execution_status", entry.ExecutionStatus);
        AddJsonParameter(command, "arguments", entry.Arguments.GetRawText());
        AddJsonParameter(command, "output", entry.Output?.GetRawText());
        AddParameter(command, "error_code", entry.ErrorCode);
        AddParameter(command, "error_message", entry.ErrorMessage);
        AddParameter(command, "created_at_utc", entry.CreatedAtUtc);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        return await dataSourceProvider.OpenConnectionAsync(cancellationToken);
    }

    private static void AddParameter(NpgsqlCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static void AddJsonParameter(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, NpgsqlDbType.Jsonb, value is null ? DBNull.Value : value);
    }
}
