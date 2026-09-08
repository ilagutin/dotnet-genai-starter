using System.Collections.Concurrent;
using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Tools.Execute;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [DockerAvailableFact]
    public async Task GovernedSchemaInvalidArgumentLimitsPersistOnlyContentFreeErrors()
    {
        const string secretKey = "postgresSyntheticArgumentKeyMarker";
        const string secretValue = "postgres-synthetic-argument-value-marker";
        var tool = new SchemaValidationProbeTool();
        var logs = new CapturingLoggerProvider();
        using var scope = await CreateScopeAsync(services =>
        {
            services.AddSingleton<IAgentToolRegistry>(new SingleToolRegistry(tool));
            services.AddSingleton<IUserContext>(new TestUserContext());
            services.AddLogging(builder => builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddProvider(logs));
        });
        await CleanToolAuditTableAsync(scope.ConnectionString);
        var dispatcher = scope.Services.GetRequiredService<IApplicationDispatcher>();
        var arguments = new[]
        {
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                [$"{secretKey}{new string('k', 65536)}"] = secretValue
            }),
            ParseJson(NestedArguments(33, secretKey, secretValue), maxDepth: 128)
        };

        foreach (var argument in arguments)
        {
            var response = await dispatcher.DispatchAsync<ExecuteToolCommand, ExecuteToolResponse>(
                new ExecuteToolCommand(tool.Definition.Name, argument),
                TestContext.Current.CancellationToken);
            Assert.Equal(ToolExecutionStatus.ValidationFailed, response.ExecutionStatus);
            Assert.Equal("schema_invalid", response.ErrorCode);
            Assert.Equal("Tool arguments do not match the declared schema at /", response.ErrorMessage);
        }

        Assert.Equal(0, tool.SemanticValidationCalls);
        Assert.Equal(0, tool.ExecutionCalls);
        var persisted = await ReadAllAuditEntriesAsync(scope.ConnectionString);
        Assert.Equal(2, persisted.Count);
        foreach (var entry in persisted)
        {
            Assert.Equal("Invalid", entry.ValidationStatus);
            Assert.Equal("ValidationFailed", entry.ExecutionStatus);
            Assert.Equal("schema_invalid", entry.ErrorCode);
            Assert.Equal("{}", entry.ArgumentsJson);
            Assert.Null(entry.OutputJson);
            Assert.Equal("Tool arguments do not match the declared schema at /", entry.ErrorMessage);
            Assert.InRange(entry.ErrorMessage!.Length, 1, 256);
        }

        Assert.Contains(logs.Entries, static entry => entry.EventId == 3001);
        var observableText = string.Join('|', persisted.SelectMany(static entry => new[]
        {
            entry.ArgumentsJson, entry.OutputJson, entry.ErrorCode, entry.ErrorMessage
        }).Concat(logs.Entries.Select(static entry => entry.Message)));
        Assert.DoesNotContain(secretKey, observableText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, observableText, StringComparison.Ordinal);
    }

    [DockerAvailableFact]
    public async Task GovernedExternalAuditPersistence_StoresOnlyMetadataForExecutionAndRejection()
    {
        const string secret = "postgres-external-secret-marker";
        var registry = new MetadataOnlyToolRegistry(secret);
        using var scope = await CreateScopeAsync(services =>
        {
            services.AddSingleton<IAgentToolRegistry>(registry);
            services.AddSingleton<IUserContext>(new TestUserContext());
        });
        await CleanToolAuditTableAsync(scope.ConnectionString);
        var dispatcher = scope.Services.GetRequiredService<IApplicationDispatcher>();
        var arguments = JsonSerializer.SerializeToElement(new { nested = new { secret } });

        await dispatcher.DispatchAsync<ExecuteToolCommand, ExecuteToolResponse>(
            new ExecuteToolCommand("external_lookup", arguments),
            TestContext.Current.CancellationToken);
        await dispatcher.DispatchAsync<ExecuteToolCommand, ExecuteToolResponse>(
            new ExecuteToolCommand("mcp_RunSqlQuery", arguments),
            TestContext.Current.CancellationToken);

        var persisted = await ReadAllAuditEntriesAsync(scope.ConnectionString);
        Assert.Equal(2, persisted.Count);
        foreach (var entry in persisted)
        {
            using var argumentsJson = JsonDocument.Parse(entry.ArgumentsJson);
            Assert.Equal(["contentOmitted", "utf8Bytes"], Keys(argumentsJson.RootElement));
            Assert.True(argumentsJson.RootElement.GetProperty("contentOmitted").GetBoolean());
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(arguments.GetRawText()),
                argumentsJson.RootElement.GetProperty("utf8Bytes").GetInt32());
            Assert.Null(entry.ErrorMessage);
        }

        var success = Assert.Single(persisted, static entry => entry.ToolName == "external_lookup");
        using (var outputJson = JsonDocument.Parse(success.OutputJson!))
        {
            Assert.Equal(
                ["contentOmitted", "returnedUtf8Bytes", "sourceUtf8Bytes", "truncated"],
                Keys(outputJson.RootElement));
            Assert.False(outputJson.RootElement.GetProperty("truncated").GetBoolean());
        }

        var rejected = Assert.Single(persisted, static entry => entry.ToolName == "mcp_RunSqlQuery");
        Assert.Equal("tool_forbidden", rejected.ErrorCode);
        Assert.Equal("Rejected", rejected.ExecutionStatus);
        Assert.Null(rejected.OutputJson);
        Assert.DoesNotContain(secret, string.Join('|', persisted.SelectMany(static entry => new[]
        {
            entry.ArgumentsJson,
            entry.OutputJson,
            entry.ErrorCode,
            entry.ErrorMessage
        })), StringComparison.Ordinal);
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

    private async Task<RepositoryScope> CreateScopeAsync(Action<IServiceCollection>? configure = null)
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
        configure?.Invoke(services);
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
                   execution_status, arguments::text, output::text, error_code, error_message
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
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
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
                   execution_status, arguments::text, output::text, error_code, error_message
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
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return entries;
    }

    private static async Task<IReadOnlyList<PersistedToolAuditEntry>> ReadAllAuditEntriesAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT conversation_id, tenant_id, user_id, tool_call_id, tool_name, schema_version,
                   policy_version, validation_status, policy_decision, approval_state,
                   execution_status, arguments::text, output::text, error_code, error_message
            FROM genai.tool_audit_logs
            ORDER BY created_at_utc;
            """, connection);
        var entries = new List<PersistedToolAuditEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    private static PersistedToolAuditEntry ReadEntry(NpgsqlDataReader reader)
    {
        return new PersistedToolAuditEntry(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    private static string[] Keys(JsonElement value)
    {
        return value.EnumerateObject().Select(static property => property.Name).Order().ToArray();
    }

    private static string NestedArguments(int levels, string key, string leafValue)
    {
        var arguments = JsonSerializer.Serialize(leafValue);
        for (var index = 0; index < levels; index++)
        {
            arguments = JsonSerializer.Serialize(new Dictionary<string, JsonElement>
            {
                [$"{key}{index}"] = ParseJson(arguments, maxDepth: 128)
            });
        }

        return arguments;
    }

    private static JsonElement ParseJson(string json, int maxDepth = 64)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maxDepth });
        return document.RootElement.Clone();
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

    private sealed class SchemaValidationProbeTool : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "SchemaValidationProbe",
            "Integration-test schema probe.",
            "v1",
            Json("""{"type":"object"}"""));

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed("Integration-test probe.");

        public int SemanticValidationCalls { get; private set; }

        public int ExecutionCalls { get; private set; }

        public ToolValidationResult Validate(JsonElement arguments)
        {
            SemanticValidationCalls++;
            return ToolValidationResult.Valid(arguments.Clone());
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            ExecutionCalls++;
            return Task.FromResult(new ToolExecutionResult(ToolExecutionStatus.Succeeded, Json("{}")));
        }

        private static JsonElement Json(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class SingleToolRegistry(IAgentTool tool) : IAgentToolRegistry
    {
        public IReadOnlyList<IAgentTool> GetAvailableTools() => [tool];
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose() { }
    }

    private sealed class CapturingLogger(ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new CapturedLogEntry(eventId.Id, formatter(state, exception)));
    }

    private sealed record CapturedLogEntry(int EventId, string Message);

    private sealed class MetadataOnlyToolRegistry(string secret) : IAgentToolRegistry
    {
        public IReadOnlyList<IAgentTool> GetAvailableTools() =>
        [
            new MetadataOnlyTool("external_lookup", secret),
            new MetadataOnlyTool("mcp_RunSqlQuery", secret)
        ];
    }

    private sealed class MetadataOnlyTool(string name, string secret) : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            name,
            "integration external probe",
            "snapshot-v1",
            Json("""{"type":"object"}"""));

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed("integration probe");

        public ToolAuditContentPolicy AuditContentPolicy => ToolAuditContentPolicy.MetadataOnly;

        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(arguments.Clone());

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            var output = JsonSerializer.SerializeToElement(new { content = secret });
            var bytes = System.Text.Encoding.UTF8.GetByteCount(output.GetRawText());
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                output,
                PayloadMetadata: new ToolExecutionPayloadMetadata(bytes, bytes, false)));
        }

        private static JsonElement Json(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class TestUserContext : IUserContext
    {
        public bool IsAuthenticated => true;
        public string? UserId => "alice";
        public string? TenantId => "tenant-a";
        public IReadOnlyCollection<string> Roles { get; } = ["developer"];
        public IReadOnlyCollection<string> Groups { get; } = ["demo"];
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
        string? ErrorCode,
        string? ErrorMessage);
}
