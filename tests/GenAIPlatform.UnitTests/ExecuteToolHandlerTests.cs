using System.Text.Json;
using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Agentic.Chat;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Tools.Execute;
using GenAIPlatform.Application.Agentic.Tools.Execution;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Domain.Prompts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GenAIPlatform.UnitTests;

public sealed class ExecuteToolHandlerTests
{
    [Fact]
    public async Task HandleAsync_ExecutesSafeToolAndAuditsWithBackendSchemaVersion()
    {
        var audit = new CapturingToolAuditLogRepository();
        var dispatcher = CreateDispatcher(audit);

        var response = await dispatcher.DispatchAsync<ExecuteToolCommand, ExecuteToolResponse>(
            new ExecuteToolCommand(
                "GetCurrentUserProfile",
                EmptyArguments(),
                "model-proposed-v9"),
            CancellationToken.None);

        Assert.Equal("GetCurrentUserProfile", response.ToolName);
        Assert.Equal("model-proposed-v9", response.SchemaVersion);
        Assert.Equal("Allowed", response.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.Succeeded, response.ExecutionStatus);
        using var result = JsonDocument.Parse(response.Result!);
        Assert.Equal("alice", result.RootElement.GetProperty("userId").GetString());

        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("GetCurrentUserProfile", auditEntry.ToolName);
        Assert.Equal("v1", auditEntry.SchemaVersion);
        Assert.Equal("Valid", auditEntry.ValidationStatus);
        Assert.Equal("Allowed", auditEntry.PolicyDecision);
        Assert.Equal("NotRequired", auditEntry.ApprovalState);
        Assert.Equal("Succeeded", auditEntry.ExecutionStatus);
        Assert.Equal("tenant-a", auditEntry.TenantId);
        Assert.Equal("alice", auditEntry.UserId);
        Assert.StartsWith("tools-execute-", auditEntry.CorrelationId, StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, auditEntry.ConversationId);
    }

    [Fact]
    public async Task HandleAsync_RejectsUnknownToolAndAuditsFallbackSchemaVersion()
    {
        var audit = new CapturingToolAuditLogRepository();
        var dispatcher = CreateDispatcher(audit);

        var response = await dispatcher.DispatchAsync<ExecuteToolCommand, ExecuteToolResponse>(
            new ExecuteToolCommand(
                "NoSuchTool",
                EmptyArguments(),
                "model-proposed-v9"),
            CancellationToken.None);

        Assert.Equal("NoSuchTool", response.ToolName);
        Assert.Equal("UnknownTool", response.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.Rejected, response.ExecutionStatus);
        Assert.Equal("tool_forbidden", response.ErrorCode);

        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("NoSuchTool", auditEntry.ToolName);
        Assert.Equal("model-proposed-v9", auditEntry.SchemaVersion);
        Assert.Equal("Invalid", auditEntry.ValidationStatus);
        Assert.Equal("UnknownTool", auditEntry.PolicyDecision);
        Assert.Equal("Rejected", auditEntry.ExecutionStatus);
        Assert.Equal("tool_forbidden", auditEntry.ErrorCode);
    }

    [Fact]
    public async Task HandleAsync_RejectsRiskyToolWithoutApprovalAndAuditsRequirement()
    {
        var audit = new CapturingToolAuditLogRepository();
        var dispatcher = CreateDispatcher(audit);

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
            CancellationToken.None);

        Assert.Equal("RequiresApproval", response.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.ApprovalRequired, response.ExecutionStatus);
        Assert.Equal("approval_required", response.ErrorCode);
        Assert.Null(response.Result);

        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("DraftEmail", auditEntry.ToolName);
        Assert.Equal("v1", auditEntry.SchemaVersion);
        Assert.Equal("Valid", auditEntry.ValidationStatus);
        Assert.Equal("RequiresApproval", auditEntry.PolicyDecision);
        Assert.Equal("Required", auditEntry.ApprovalState);
        Assert.Equal("ApprovalRequired", auditEntry.ExecutionStatus);
        Assert.Equal("approval_required", auditEntry.ErrorCode);
        Assert.Null(auditEntry.Output);
    }

    [Fact]
    public async Task ChatExecutor_KeepsResponseSchemaFromModelButAuditsBackendSchemaVersion()
    {
        var audit = new CapturingToolAuditLogRepository();
        var executor = new AgentToolExecutor(
            new GovernedAgentToolExecutor(
                new ToolPolicy(),
                new AgentToolArgumentValidator(),
                new AgentToolAuditLogWriter(audit, TimeProvider.System)),
            NullLogger<AgentToolExecutor>.Instance);
        var session = new AgenticChatSession(
            Guid.NewGuid(),
            "tenant-a",
            "alice",
            new ModelGatewayRequestSettings("agent-test", "mock-chat", 0.2, 1024),
            new AgenticChatOptions(),
            [new VersionedSafeTool()],
            new AgenticPromptMessages([], new PromptMetadata("agentic-chat", "v1", "hash")),
            ApproveRiskyTools: false);
        var toolCall = new AiToolCall(
            "call-1",
            "VersionedSafe",
            "model-proposed-v9",
            EmptyArguments());

        var result = await executor.ExecuteAsync(
            session,
            toolCall,
            CancellationToken.None);

        Assert.Equal("model-proposed-v9", result.SchemaVersion);
        Assert.Equal(ToolExecutionStatus.Succeeded, result.ExecutionStatus);

        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("backend-owned-v2", auditEntry.SchemaVersion);
        Assert.Equal("Succeeded", auditEntry.ExecutionStatus);
    }

    private static IApplicationDispatcher CreateDispatcher(CapturingToolAuditLogRepository auditRepository)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUserContext>(new TestUserContext());
        services.AddSingleton<IToolAuditLogRepository>(auditRepository);
        services.AddTestApplication(new Microsoft.Extensions.Configuration.ConfigurationManager());

        return services
            .BuildServiceProvider()
            .GetRequiredService<IApplicationDispatcher>();
    }

    private static JsonElement EmptyArguments()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class CapturingToolAuditLogRepository : IToolAuditLogRepository
    {
        public List<ToolAuditLogEntry> Entries { get; } = [];

        public Task AddAsync(ToolAuditLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
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

    private sealed class VersionedSafeTool : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "VersionedSafe",
            "Safe tool with a backend-owned schema version.",
            "backend-owned-v2",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Safe test tool.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "VersionedSafe expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                JsonSerializer.SerializeToElement(new { value = "ok" })));
        }
    }
}
