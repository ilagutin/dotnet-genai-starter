using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools.Execution;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace GenAIPlatform.UnitTests;

public sealed partial class ExternalMcpAgentToolSourceTests
{
    [Fact]
    public async Task ExecuteAsync_UsesFrozenSchemaThroughGovernanceApprovalAndMetadataAudit()
    {
        const string frozenSchema = """
        {
          "$defs": { "pattern": { "type": "string" } },
          "type": "object",
          "properties": { "pattern": { "$ref": "#/$defs/pattern" } },
          "required": [ "pattern" ],
          "additionalProperties": false
        }
        """;
        var client = FakeExternalMcpClient.WithTools(Tool("echo", "Echo.", frozenSchema));
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManager(factory, Server("Server"));
        await manager.RefreshAsync(CancellationToken.None);
        var tool = Assert.Single(new ExternalMcpAgentToolSource(manager, NullLoggerFactory.Instance).GetAvailableTools());
        var frozenVersion = tool.Definition.SchemaVersion;
        client.Tools = [Tool("echo", "Changed.", "{\"type\":\"array\"}")];
        var audit = new CapturingAuditRepository();
        var executor = new GovernedAgentToolExecutor(
            new ToolPolicy(),
            new AgentToolArgumentValidator(),
            new AgentToolAuditLogWriter(audit, TimeProvider.System));

        var result = await executor.ExecuteAsync(
            new AgentToolExecutionRequest(
                "call-1",
                tool.Definition.Name,
                frozenVersion,
                JsonSerializer.SerializeToElement(new { pattern = "ordinary-name" }),
                [tool],
                new AgentToolExecutionContext(
                    Guid.NewGuid(), "tenant-a", "alice", "schema-test", "tool-policy-v1", ApproveRiskyTools: true)),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.ExecutionStatus);
        Assert.Equal(1, client.CallCount);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("SimulatedApproved", entry.ApprovalState);
        Assert.Equal(frozenVersion, entry.SchemaVersion);
        Assert.True(entry.Arguments.GetProperty("contentOmitted").GetBoolean());
        Assert.Equal(27, entry.Arguments.GetProperty("utf8Bytes").GetInt32());
        Assert.DoesNotContain("ordinary-name", entry.Arguments.GetRawText(), StringComparison.Ordinal);
        Assert.Null(entry.Output);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsUnsafeFrozenSchemaBeforeApprovalOrExternalCall()
    {
        const string unsafeSchema = """
        {
          "type": "object",
          "properties": { "value": { "allOf": [ { "pattern": ".*" } ] } }
        }
        """;
        var client = FakeExternalMcpClient.WithTools(Tool("echo", "Echo.", unsafeSchema));
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManager(factory, Server("Server"));
        await manager.RefreshAsync(CancellationToken.None);
        var tool = Assert.Single(new ExternalMcpAgentToolSource(manager, NullLoggerFactory.Instance).GetAvailableTools());
        var audit = new CapturingAuditRepository();
        var executor = new GovernedAgentToolExecutor(
            new ToolPolicy(),
            new AgentToolArgumentValidator(),
            new AgentToolAuditLogWriter(audit, TimeProvider.System));

        var result = await executor.ExecuteAsync(
            new AgentToolExecutionRequest(
                "call-unsafe",
                tool.Definition.Name,
                tool.Definition.SchemaVersion,
                JsonSerializer.SerializeToElement(new { value = "private-value" }),
                [tool],
                new AgentToolExecutionContext(
                    Guid.NewGuid(), "tenant-a", "alice", "schema-test", "tool-policy-v1", ApproveRiskyTools: false)),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.ValidationFailed, result.ExecutionStatus);
        Assert.Equal(AgentToolArgumentValidator.SchemaDefinitionInvalidCode, result.ErrorCode);
        Assert.Equal(0, client.CallCount);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("NotRequired", entry.ApprovalState);
        Assert.True(entry.Arguments.GetProperty("contentOmitted").GetBoolean());
        Assert.DoesNotContain("private-value", entry.Arguments.GetRawText(), StringComparison.Ordinal);
        Assert.Null(entry.Output);
    }
}
