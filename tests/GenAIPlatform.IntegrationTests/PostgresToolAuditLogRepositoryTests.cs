using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresToolAuditLogRepositoryTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task ToolAuditLogPersistence_StoresPolicyValidationApprovalAndExecutionState()
    {
        using var scope = await CreateScopeAsync();
        await CleanToolAuditTableAsync(scope.ConnectionString);
        var repository = scope.Services.GetRequiredService<IToolAuditLogRepository>();
        using var arguments = JsonDocument.Parse("""{"title":"Help","description":"Need help"}""");
        using var output = JsonDocument.Parse("""{"ticketId":"SUP-00001","status":"Created"}""");
        var id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var conversationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        await repository.AddAsync(
            new ToolAuditLogEntry(
                id,
                conversationId,
                "tenant-a",
                "alice",
                "tool-audit-test",
                "call-1",
                "CreateSupportTicket",
                "v1",
                "tool-policy-v1",
                "Valid",
                "Allowed",
                "NotRequired",
                "Succeeded",
                arguments.RootElement.Clone(),
                output.RootElement.Clone(),
                ErrorCode: null,
                ErrorMessage: null,
                DateTimeOffset.Parse("2026-05-15T12:00:00Z")),
            TestContext.Current.CancellationToken);

        var persisted = await ReadAuditEntryAsync(scope.ConnectionString, id);

        Assert.Equal(conversationId, persisted.ConversationId);
        Assert.Equal("tenant-a", persisted.TenantId);
        Assert.Equal("alice", persisted.UserId);
        Assert.Equal("CreateSupportTicket", persisted.ToolName);
        Assert.Equal("v1", persisted.SchemaVersion);
        Assert.Equal("tool-policy-v1", persisted.PolicyVersion);
        Assert.Equal("Valid", persisted.ValidationStatus);
        Assert.Equal("Allowed", persisted.PolicyDecision);
        Assert.Equal("NotRequired", persisted.ApprovalState);
        Assert.Equal("Succeeded", persisted.ExecutionStatus);
        Assert.Contains("Need help", persisted.ArgumentsJson);
        Assert.Contains("SUP-00001", persisted.OutputJson);
    }

    [DockerAvailableFact]
    public async Task ToolAuditLogPersistence_StoresRejectedApprovalRequiredFailedAndNotExecutedStates()
    {
        using var scope = await CreateScopeAsync();
        await CleanToolAuditTableAsync(scope.ConnectionString);
        var repository = scope.Services.GetRequiredService<IToolAuditLogRepository>();
        var conversationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        using var arguments = JsonDocument.Parse("""{"demo":true}""");
        using var output = JsonDocument.Parse("""{"draftId":"DRAFT-1","sent":false}""");

        var entries = new[]
        {
            CreateEntry(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                conversationId,
                "call-approved",
                "DraftEmail",
                "Valid",
                "RequiresApproval",
                "SimulatedApproved",
                "Succeeded",
                arguments.RootElement.Clone(),
                output.RootElement.Clone()),
            CreateEntry(
                Guid.Parse("10000000-0000-0000-0000-000000000002"),
                conversationId,
                "call-required",
                "DraftEmail",
                "Valid",
                "RequiresApproval",
                "Required",
                "ApprovalRequired",
                arguments.RootElement.Clone(),
                null,
                "approval_required"),
            CreateEntry(
                Guid.Parse("10000000-0000-0000-0000-000000000003"),
                conversationId,
                "call-rejected",
                "DeleteDocument",
                "Valid",
                "Forbidden",
                "NotRequired",
                "Rejected",
                arguments.RootElement.Clone(),
                null,
                "tool_forbidden"),
            CreateEntry(
                Guid.Parse("10000000-0000-0000-0000-000000000004"),
                conversationId,
                "call-failed",
                "CreateSupportTicket",
                "Valid",
                "Allowed",
                "NotRequired",
                "Failed",
                arguments.RootElement.Clone(),
                null,
                "tool_execution_failed"),
            CreateEntry(
                Guid.Parse("10000000-0000-0000-0000-000000000005"),
                conversationId,
                "call-not-executed",
                "GetCurrentUserProfile",
                "Valid",
                "Allowed",
                "NotRequired",
                "NotExecuted",
                arguments.RootElement.Clone(),
                null,
                "budget_exceeded")
        };

        foreach (var entry in entries)
        {
            await repository.AddAsync(entry, TestContext.Current.CancellationToken);
        }

        var persisted = await ReadAuditEntriesAsync(scope.ConnectionString, conversationId);

        Assert.Equal(5, persisted.Count);
        Assert.Contains(persisted, entry =>
            entry.ToolCallId == "call-approved" &&
            entry.ApprovalState == "SimulatedApproved" &&
            entry.ExecutionStatus == "Succeeded" &&
            entry.OutputJson is not null);
        Assert.Contains(persisted, entry =>
            entry.ToolCallId == "call-required" &&
            entry.ApprovalState == "Required" &&
            entry.ExecutionStatus == "ApprovalRequired" &&
            entry.ErrorCode == "approval_required");
        Assert.Contains(persisted, entry =>
            entry.ToolCallId == "call-rejected" &&
            entry.PolicyDecision == "Forbidden" &&
            entry.ExecutionStatus == "Rejected" &&
            entry.ErrorCode == "tool_forbidden");
        Assert.Contains(persisted, entry =>
            entry.ToolCallId == "call-failed" &&
            entry.ExecutionStatus == "Failed" &&
            entry.ErrorCode == "tool_execution_failed");
        Assert.Contains(persisted, entry =>
            entry.ToolCallId == "call-not-executed" &&
            entry.ExecutionStatus == "NotExecuted" &&
            entry.ErrorCode == "budget_exceeded");
    }

    private static ToolAuditLogEntry CreateEntry(
        Guid id,
        Guid conversationId,
        string toolCallId,
        string toolName,
        string validationStatus,
        string policyDecision,
        string approvalState,
        string executionStatus,
        JsonElement arguments,
        JsonElement? output,
        string? errorCode = null)
    {
        return new ToolAuditLogEntry(
            id,
            conversationId,
            "tenant-a",
            "alice",
            "tool-audit-test",
            toolCallId,
            toolName,
            "v1",
            "tool-policy-v1",
            validationStatus,
            policyDecision,
            approvalState,
            executionStatus,
            arguments,
            output,
            errorCode,
            errorCode is null ? null : "tool audit test state",
            DateTimeOffset.Parse("2026-05-15T12:00:00Z"));
    }

    private async Task<RepositoryScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = connectionString,
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        var serviceProvider = services.BuildServiceProvider();

        return new RepositoryScope(serviceProvider, connectionString);
    }

    private static async Task CleanToolAuditTableAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("TRUNCATE TABLE genai.tool_audit_logs;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PersistedToolAuditEntry> ReadAuditEntryAsync(
        string connectionString,
        Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT conversation_id, tenant_id, user_id, tool_call_id, tool_name, schema_version,
                   policy_version, validation_status, policy_decision, approval_state,
                   execution_status, arguments::text, output::text, error_code
            FROM genai.tool_audit_logs
            WHERE id = @id;
            """, connection);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Tool audit entry was not found.");
        }

        return new PersistedToolAuditEntry(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));
    }

    private static async Task<IReadOnlyList<PersistedToolAuditEntry>> ReadAuditEntriesAsync(
        string connectionString,
        Guid conversationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT conversation_id, tenant_id, user_id, tool_call_id, tool_name, schema_version,
                   policy_version, validation_status, policy_decision, approval_state,
                   execution_status, arguments::text, output::text, error_code
            FROM genai.tool_audit_logs
            WHERE conversation_id = @conversation_id
            ORDER BY tool_call_id;
            """, connection);
        command.Parameters.AddWithValue("conversation_id", conversationId);

        var entries = new List<PersistedToolAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new PersistedToolAuditEntry(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        }

        return entries;
    }

    private sealed record RepositoryScope(
        ServiceProvider Services,
        string ConnectionString)
        : IDisposable
    {
        public void Dispose()
        {
            Services.Dispose();
        }
    }

    private sealed record PersistedToolAuditEntry(
        Guid ConversationId,
        string TenantId,
        string UserId,
        string ToolCallId,
        string ToolName,
        string SchemaVersion,
        string PolicyVersion,
        string ValidationStatus,
        string PolicyDecision,
        string ApprovalState,
        string ExecutionStatus,
        string ArgumentsJson,
        string? OutputJson,
        string? ErrorCode);
}
