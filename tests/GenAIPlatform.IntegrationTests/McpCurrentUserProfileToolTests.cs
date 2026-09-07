extern alias McpHost;
using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools.Execute;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Domain.Agentic;
using McpHost::GenAIPlatform.Mcp;
using McpHost::GenAIPlatform.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class McpCurrentUserProfileToolTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task GetCurrentUserProfileAsync_ExecutesGovernedToolAndWritesAuditRow()
    {
        using var scope = await CreateScopeAsync();
        await CleanToolAuditTableAsync(scope.ConnectionString);
        var tool = new CurrentUserProfileTool(
            scope.Services.GetRequiredService<IApplicationDispatcher>());

        var markdown = await tool.GetCurrentUserProfileAsync(TestContext.Current.CancellationToken);

        Assert.Contains("userId: mcp-user", markdown, StringComparison.Ordinal);
        Assert.Contains("tenantId: local", markdown, StringComparison.Ordinal);
        Assert.Contains("roles: developer", markdown, StringComparison.Ordinal);

        var auditEntry = Assert.Single(await ReadAuditEntriesAsync(scope.ConnectionString));
        Assert.Equal("GetCurrentUserProfile", auditEntry.ToolName);
        Assert.Equal("v1", auditEntry.SchemaVersion);
        Assert.Equal("Valid", auditEntry.ValidationStatus);
        Assert.Equal("Allowed", auditEntry.PolicyDecision);
        Assert.Equal("NotRequired", auditEntry.ApprovalState);
        Assert.Equal("Succeeded", auditEntry.ExecutionStatus);
        Assert.Equal("local", auditEntry.TenantId);
        Assert.Equal("mcp-user", auditEntry.UserId);
        Assert.StartsWith("tools-execute-", auditEntry.CorrelationId, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, auditEntry.ConversationId);
        Assert.Contains("mcp-user", auditEntry.OutputJson);
    }

    [DockerAvailableFact]
    public async Task ExecuteToolCommand_RiskyToolReturnsApprovalRequiredAndWritesAuditRow()
    {
        using var scope = await CreateScopeAsync();
        await CleanToolAuditTableAsync(scope.ConnectionString);
        var dispatcher = scope.Services.GetRequiredService<IApplicationDispatcher>();

        var response = await dispatcher.DispatchAsync<ExecuteToolCommand, ExecuteToolResponse>(
            new ExecuteToolCommand(
                "DraftEmail",
                Json("""
                {
                  "to": "a@example.test",
                  "subject": "Hello",
                  "body": "Draft only"
                }
                """)),
            TestContext.Current.CancellationToken);

        Assert.Equal("RequiresApproval", response.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.ApprovalRequired, response.ExecutionStatus);
        Assert.Equal("approval_required", response.ErrorCode);

        var auditEntry = Assert.Single(await ReadAuditEntriesAsync(scope.ConnectionString));
        Assert.Equal("DraftEmail", auditEntry.ToolName);
        Assert.Equal("v1", auditEntry.SchemaVersion);
        Assert.Equal("Valid", auditEntry.ValidationStatus);
        Assert.Equal("RequiresApproval", auditEntry.PolicyDecision);
        Assert.Equal("Required", auditEntry.ApprovalState);
        Assert.Equal("ApprovalRequired", auditEntry.ExecutionStatus);
        Assert.Equal("approval_required", auditEntry.ErrorCode);
        Assert.Null(auditEntry.OutputJson);
    }

    private async Task<RepositoryScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.mcp.test.json", optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = connectionString,
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGenAIPlatformMcp(configuration);
        var serviceProvider = services.BuildServiceProvider();
        var serviceScope = serviceProvider.CreateScope();

        return new RepositoryScope(serviceProvider, serviceScope, serviceScope.ServiceProvider, connectionString);
    }

    private static async Task CleanToolAuditTableAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("TRUNCATE TABLE genai.tool_audit_logs;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<PersistedToolAuditEntry>> ReadAuditEntriesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT conversation_id, tenant_id, user_id, correlation_id, tool_name, schema_version,
                   validation_status, policy_decision, approval_state, execution_status,
                   output::text, error_code
            FROM genai.tool_audit_logs
            ORDER BY created_at_utc;
            """, connection);

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
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return entries;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record RepositoryScope(
        ServiceProvider RootProvider,
        IServiceScope Scope,
        IServiceProvider Services,
        string ConnectionString)
        : IDisposable
    {
        public void Dispose()
        {
            Scope.Dispose();
            RootProvider.Dispose();
        }
    }

    private sealed record PersistedToolAuditEntry(
        Guid ConversationId,
        string TenantId,
        string UserId,
        string CorrelationId,
        string ToolName,
        string SchemaVersion,
        string ValidationStatus,
        string PolicyDecision,
        string ApprovalState,
        string ExecutionStatus,
        string? OutputJson,
        string? ErrorCode);
}
