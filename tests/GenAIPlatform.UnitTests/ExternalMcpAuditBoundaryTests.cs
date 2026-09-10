using System.Text;
using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Tools.Execution;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.UnitTests;

public sealed class ExternalMcpAuditBoundaryTests
{
    private const string Secret = "audit-secret-marker";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_MetadataOnlySuccessAndRemoteErrorKeepClosedAuditShape(bool remoteError)
    {
        var output = JsonSerializer.SerializeToElement(new
        {
            text = Secret,
            nested = new { document = Secret }
        });
        var metadata = new ToolExecutionPayloadMetadata(77, 77, false);
        var tool = new MetadataOnlyTool(
            ToolPolicyMetadata.ApprovalRequired("approval required"),
            new ToolExecutionResult(
                remoteError ? ToolExecutionStatus.Failed : ToolExecutionStatus.Succeeded,
                output,
                remoteError ? "mcp_tool_error" : null,
                remoteError ? Secret : null,
                metadata));
        var audit = new CapturingAuditRepository();

        var result = await ExecuteAsync(tool, audit, approve: true);

        Assert.Equal(output.GetRawText(), result.Output!.Value.GetRawText());
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(["contentOmitted", "utf8Bytes"], Keys(entry.Arguments));
        Assert.Equal(Encoding.UTF8.GetByteCount(Arguments().GetRawText()), entry.Arguments.GetProperty("utf8Bytes").GetInt32());
        Assert.Equal(["contentOmitted", "returnedUtf8Bytes", "sourceUtf8Bytes", "truncated"], Keys(entry.Output!.Value));
        Assert.Equal(77, entry.Output.Value.GetProperty("sourceUtf8Bytes").GetInt32());
        Assert.Null(entry.ErrorMessage);
        Assert.DoesNotContain(Secret, entry.Arguments.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, entry.Output.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcp_RunSqlQuery", true, "tool_forbidden", "Rejected")]
    [InlineData("external_lookup", false, "approval_required", "ApprovalRequired")]
    public async Task ExecuteAsync_NonExecutionUsesMetadataOnlyProjection(
        string toolName,
        bool approve,
        string errorCode,
        string executionStatus)
    {
        var tool = new MetadataOnlyTool(
            ToolPolicyMetadata.ApprovalRequired("approval required"),
            new ToolExecutionResult(ToolExecutionStatus.Succeeded, Json("{}")),
            toolName);
        var audit = new CapturingAuditRepository();

        await ExecuteAsync(tool, audit, approve);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(errorCode, entry.ErrorCode);
        Assert.Equal(executionStatus, entry.ExecutionStatus);
        Assert.Equal(["contentOmitted", "utf8Bytes"], Keys(entry.Arguments));
        Assert.Null(entry.Output);
        Assert.Null(entry.ErrorMessage);
        Assert.Equal(0, tool.ExecuteCalls);
    }

    [Fact]
    public async Task ExecuteAsync_MetadataOnlyFailureWithoutProviderPayloadHasNullAuditOutput()
    {
        var tool = new MetadataOnlyTool(
            ToolPolicyMetadata.Allowed("test"),
            new ToolExecutionResult(
                ToolExecutionStatus.Failed,
                Json("{}"),
                "mcp_tool_timeout",
                Secret,
                PayloadMetadata: null));
        var audit = new CapturingAuditRepository();

        var result = await ExecuteAsync(tool, audit, approve: false);

        Assert.Equal("{}", result.Output!.Value.GetRawText());
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("mcp_tool_timeout", entry.ErrorCode);
        Assert.Null(entry.Output);
        Assert.Null(entry.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotInferMetadataOnlyFromMcpName()
    {
        var tool = new ContentAuditTool();
        var audit = new CapturingAuditRepository();

        await ExecuteAsync(tool, audit, approve: false);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(Secret, entry.Arguments.GetProperty("nested").GetProperty("secret").GetString());
        Assert.Equal("built-in", entry.Output!.Value.GetProperty("value").GetString());
    }

    private static async Task<AgentToolExecutionResult> ExecuteAsync(
        IAgentTool tool,
        CapturingAuditRepository audit,
        bool approve)
    {
        var executor = new GovernedAgentToolExecutor(
            new ToolPolicy(),
            new AgentToolArgumentValidator(),
            new AgentToolAuditLogWriter(audit, TimeProvider.System));
        return await executor.ExecuteAsync(
            new AgentToolExecutionRequest(
                "call-1",
                tool.Definition.Name,
                "model-v9",
                Arguments(),
                [tool],
                new AgentToolExecutionContext(
                    Guid.NewGuid(),
                    "tenant-a",
                    "alice",
                    "correlation",
                    "policy-v1",
                    approve)),
            CancellationToken.None);
    }

    private static string[] Keys(JsonElement value)
    {
        return value.EnumerateObject().Select(static property => property.Name).Order().ToArray();
    }

    private static JsonElement Arguments() => JsonSerializer.SerializeToElement(new
    {
        nested = new { secret = Secret }
    });

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class MetadataOnlyTool(
        ToolPolicyMetadata policy,
        ToolExecutionResult result,
        string name = "external_lookup") : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(name, "test", "snapshot-v1", Json("""
        {"type":"object","properties":{"nested":{"type":"object"}},"required":["nested"]}
        """));

        public ToolPolicyMetadata Policy { get; } = policy;

        public ToolAuditContentPolicy AuditContentPolicy => ToolAuditContentPolicy.MetadataOnly;

        public int ExecuteCalls { get; private set; }

        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(arguments.Clone());

        public Task<ToolExecutionResult> ExecuteAsync(JsonElement sanitizedArguments, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ContentAuditTool : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "mcp_but_actually_builtin",
            "test",
            "v1",
            Json("""{"type":"object"}"""));

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed("test");

        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(arguments.Clone());

        public Task<ToolExecutionResult> ExecuteAsync(JsonElement sanitizedArguments, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                Json("""{"value":"built-in"}""")));
        }
    }

    private sealed class CapturingAuditRepository : IToolAuditLogRepository
    {
        public List<ToolAuditLogEntry> Entries { get; } = [];

        public Task AddAsync(ToolAuditLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
