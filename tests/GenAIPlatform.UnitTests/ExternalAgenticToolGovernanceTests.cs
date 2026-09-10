using System.Text.Json;
using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Agentic.Chat;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Infrastructure.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed partial class ExternalAgenticToolGovernanceTests
{
    private const string ExternalToolName = "mcp_orders_lookup";

    [Fact]
    public async Task HandleAsync_ApprovedExternalToolExecutesAndAuditsSnapshotHash()
    {
        var server = FakeExternalMcpServer.Connect([
            FakeExternalMcpToolDescriptor.RequiresApproval(
                ExternalToolName,
                "Looks up an order through the connected MCP server.",
                Schema("query"))
        ]);
        var audit = new CapturingToolAuditLogRepository();
        var model = new SequenceModelClient([
            ToolResponse(ExternalToolName, """{"query":"A-100"}""", "model-proposed-v9"),
            FinalResponse("External lookup complete.")
        ]);
        var dispatcher = CreateDispatcher(model, audit, server.Source);

        var response = await dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Lookup order A-100.", CorrelationId: "external-c2", ApproveRiskyTools: true),
            CancellationToken.None);

        var tool = Assert.Single(server.Source.SnapshotTools);
        Assert.Equal("Succeeded", response.Status);
        Assert.Equal(1, response.ToolCalls);
        Assert.Equal(1, tool.ExecuteCalls);
        Assert.Equal(1, tool.ValidateCalls);
        Assert.Equal("model-proposed-v9", Assert.Single(response.ToolResults).SchemaVersion);

        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal(ExternalToolName, auditEntry.ToolName);
        Assert.Equal(tool.Definition.SchemaVersion, auditEntry.SchemaVersion);
        Assert.StartsWith("sha256:", auditEntry.SchemaVersion, StringComparison.Ordinal);
        Assert.Equal("Valid", auditEntry.ValidationStatus);
        Assert.Equal("RequiresApproval", auditEntry.PolicyDecision);
        Assert.Equal("SimulatedApproved", auditEntry.ApprovalState);
        Assert.Equal("Succeeded", auditEntry.ExecutionStatus);
        AssertMetadataOnly(auditEntry, hasOutput: true);
    }

    [Fact]
    public async Task HandleAsync_DefaultExternalToolApprovalRequiredAuditsAllProposals()
    {
        var server = FakeExternalMcpServer.Connect([
            FakeExternalMcpToolDescriptor.RequiresApproval(ExternalToolName, "Primary external lookup.", Schema("query")),
            FakeExternalMcpToolDescriptor.RequiresApproval("mcp_orders_secondary", "Second external lookup.", Schema("query"))
        ]);
        var audit = new CapturingToolAuditLogRepository();
        var dispatcher = CreateDispatcher(
            new SequenceModelClient([
                ToolResponse(
                    (ExternalToolName, """{"query":"A-100"}"""),
                    ("mcp_orders_secondary", """{"query":"B-200"}"""))
            ]),
            audit,
            server.Source);

        var response = await dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use external tools.", CorrelationId: "external-c2"),
            CancellationToken.None);

        Assert.Equal("ApprovalRequired", response.Status);
        Assert.Equal(2, response.ToolCalls);
        Assert.All(server.Source.SnapshotTools, tool => Assert.Equal(0, tool.ExecuteCalls));
        var firstResult = Assert.Single(response.ToolResults);
        Assert.Equal(ExternalToolName, firstResult.ToolName);
        Assert.Equal(ToolExecutionStatus.ApprovalRequired, firstResult.ExecutionStatus);
        Assert.Equal("approval_required", firstResult.ErrorCode);

        Assert.Equal(2, audit.Entries.Count);
        Assert.Equal("ApprovalRequired", audit.Entries[0].ExecutionStatus);
        Assert.Equal("Required", audit.Entries[0].ApprovalState);
        Assert.Equal("NotExecuted", audit.Entries[1].ExecutionStatus);
        Assert.Equal("approval_required", audit.Entries[1].ErrorCode);
        Assert.Equal(server.Source.SnapshotTools[1].Definition.SchemaVersion, audit.Entries[1].SchemaVersion);
        Assert.All(audit.Entries, entry => AssertMetadataOnly(entry, hasOutput: false));
    }

    [Fact]
    public async Task HandleAsync_ForbiddenExternalToolRejectsAndAuditsWithoutExecution()
    {
        var server = FakeExternalMcpServer.Connect([
            FakeExternalMcpToolDescriptor.Forbidden(
                "mcp_admin_RunSqlQuery",
                "Policy-blacklisted external SQL runner.",
                Schema("query"))
        ]);
        var audit = new CapturingToolAuditLogRepository();
        var dispatcher = CreateDispatcher(
            new SequenceModelClient([
                ToolResponse("mcp_admin_RunSqlQuery", """{"query":"select 1"}""")
            ]),
            audit,
            server.Source);

        var response = await dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Run a SQL query.", CorrelationId: "external-c2", ApproveRiskyTools: true),
            CancellationToken.None);

        var tool = Assert.Single(server.Source.SnapshotTools);
        Assert.Equal("ToolRejected", response.Status);
        Assert.Equal(0, tool.ExecuteCalls);
        Assert.Equal(1, tool.ValidateCalls);
        var result = Assert.Single(response.ToolResults);
        Assert.Equal("Forbidden", result.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.Rejected, result.ExecutionStatus);
        Assert.Equal("tool_forbidden", result.ErrorCode);

        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal(tool.Definition.SchemaVersion, auditEntry.SchemaVersion);
        Assert.Equal("Valid", auditEntry.ValidationStatus);
        Assert.Equal("Forbidden", auditEntry.PolicyDecision);
        Assert.Equal("Rejected", auditEntry.ExecutionStatus);
        AssertMetadataOnly(auditEntry, hasOutput: false);
    }

    [Fact]
    public async Task HandleAsync_RealExternalMcpWrapperAppliesBlacklistBeforeApprovalPolicy()
    {
        var externalClient = FakeExternalMcpClient.WithTools(new ExternalMcpToolDescriptor(
            "RunSqlQuery",
            "External SQL runner that must be forbidden by backend policy.",
            Schema("query")));
        var manager = new ExternalMcpConnectionManager(
            Options.Create(new ExternalMcpOptions
            {
                Servers =
                [
                    new ExternalMcpServerOptions
                    {
                        Name = "Admin",
                        Command = "fake"
                    }
                ]
            }),
            new SingleExternalMcpClientFactory(externalClient),
            new AlwaysConnectMcpPolicy(),
            NullLogger<ExternalMcpConnectionManager>.Instance);
        await manager.RefreshAsync(CancellationToken.None);
        try
        {
            var source = new ExternalMcpAgentToolSource(manager, NullLoggerFactory.Instance);
            var toolName = Assert.Single(source.GetAvailableTools()).Definition.Name;
            var audit = new CapturingToolAuditLogRepository();
            var dispatcher = CreateDispatcher(
                new SequenceModelClient([
                    ToolResponse(toolName, """{"query":"select 1"}""")
                ]),
                audit,
                source);

            var response = await dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
                new AgenticChatCommand("Run a SQL query.", CorrelationId: "external-c2", ApproveRiskyTools: true),
                CancellationToken.None);

            Assert.Equal("ToolRejected", response.Status);
            Assert.Equal(0, externalClient.CallCount);
            var result = Assert.Single(response.ToolResults);
            Assert.Equal(toolName, result.ToolName);
            Assert.Equal("Forbidden", result.PolicyDecision);
            Assert.Equal(ToolExecutionStatus.Rejected, result.ExecutionStatus);
            Assert.Equal("tool_forbidden", result.ErrorCode);
            var auditEntry = Assert.Single(audit.Entries);
            Assert.Equal(toolName, auditEntry.ToolName);
            Assert.Equal("Forbidden", auditEntry.PolicyDecision);
            Assert.Equal("Rejected", auditEntry.ExecutionStatus);
        }
        finally
        {
            await new ExternalMcpHostedService(manager).StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HandleAsync_RealExternalMcpWrapperRejectsSnapshotSchemaMismatchBeforeClientCall()
    {
        var externalClient = FakeExternalMcpClient.WithTools(new ExternalMcpToolDescriptor(
            "lookup",
            "Looks up an order.",
            Json("""
            {
              "type": "object",
              "properties": { "query": { "type": "string" } },
              "required": [ "query" ],
              "additionalProperties": false
            }
            """)));
        var manager = new ExternalMcpConnectionManager(
            Options.Create(new ExternalMcpOptions
            {
                Servers = [new ExternalMcpServerOptions { Name = "Orders", Command = "fake" }]
            }),
            new SingleExternalMcpClientFactory(externalClient),
            new AlwaysConnectMcpPolicy(),
            NullLogger<ExternalMcpConnectionManager>.Instance);
        await manager.RefreshAsync(CancellationToken.None);
        try
        {
            var source = new ExternalMcpAgentToolSource(manager, NullLoggerFactory.Instance);
            var tool = Assert.Single(source.GetAvailableTools());
            var snapshotHash = tool.Definition.SchemaVersion;
            var audit = new CapturingToolAuditLogRepository();
            var dispatcher = CreateDispatcher(
                new SequenceModelClient([ToolResponse(tool.Definition.Name, """{"wrong":"secret-value"}""")]),
                audit,
                source);

            var response = await dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
                new AgenticChatCommand("Use external lookup.", CorrelationId: "external-schema", ApproveRiskyTools: true),
                CancellationToken.None);

            var result = Assert.Single(response.ToolResults);
            Assert.Equal(ToolExecutionStatus.ValidationFailed, result.ExecutionStatus);
            Assert.Equal("schema_invalid", result.ErrorCode);
            Assert.Equal(0, externalClient.CallCount);
            var entry = Assert.Single(audit.Entries);
            Assert.Equal(snapshotHash, entry.SchemaVersion);
            Assert.True(entry.Arguments.GetProperty("contentOmitted").GetBoolean());
            Assert.True(entry.Arguments.GetProperty("utf8Bytes").GetInt32() > 0);
            Assert.DoesNotContain("secret-value", entry.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            await new ExternalMcpHostedService(manager).StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HandleAsync_BudgetSkippedExternalToolStillWritesAudit()
    {
        var server = FakeExternalMcpServer.Connect([
            FakeExternalMcpToolDescriptor.RequiresApproval(ExternalToolName, "External lookup.", Schema("query"))
        ]);
        var audit = new CapturingToolAuditLogRepository();
        var dispatcher = CreateDispatcher(
            new SequenceModelClient([
                new AiModelResponse(
                    "Tool proposed with costly response.",
                    "mock-chat",
                    "mock",
                    new AiModelUsage(100, 100, 200),
                    "external-c2",
                    [new AiToolCall("call-1", ExternalToolName, "model-proposed-v9", Json("""{"query":"A-100"}"""))])
            ]),
            audit,
            server.Source,
            new AgenticChatOptions
            {
                MaxSteps = 4,
                TimeoutSeconds = 15,
                MaxToolCalls = 8,
                MaxTotalTokens = 10,
                MaxEstimatedCost = 1,
                EstimatedCostPerThousandTokens = 0,
                PolicyVersion = "tool-policy-v1"
            });

        var response = await dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use external tool.", CorrelationId: "external-c2", ApproveRiskyTools: true),
            CancellationToken.None);

        var tool = Assert.Single(server.Source.SnapshotTools);
        Assert.Equal("BudgetExceeded", response.Status);
        Assert.Empty(response.ToolResults);
        Assert.Equal(0, tool.ExecuteCalls);
        Assert.Equal(1, tool.ValidateCalls);
        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal(tool.Definition.SchemaVersion, auditEntry.SchemaVersion);
        Assert.Equal("NotExecuted", auditEntry.ExecutionStatus);
        Assert.Equal("budget_exceeded", auditEntry.ErrorCode);
        AssertMetadataOnly(auditEntry, hasOutput: false);
    }

    [Fact]
    public async Task HandleAsync_RugPullKeepsSnapshotDefinitionAndAuditsSnapshotHash()
    {
        var descriptor = FakeExternalMcpToolDescriptor.RequiresApproval(
            ExternalToolName,
            "Original snapshot description.",
            Schema("query"));
        var server = FakeExternalMcpServer.Connect([descriptor]);
        var snapshotHash = Assert.Single(server.Source.SnapshotTools).Definition.SchemaVersion;
        server.MutateTool(
            ExternalToolName,
            "Mutated server description after connect.",
            Schema("changed"));
        var audit = new CapturingToolAuditLogRepository();
        var model = new SnapshotProbeModelClient(
            ExternalToolName,
            snapshotHash,
            "Original snapshot description.");
        var dispatcher = CreateDispatcher(model, audit, server.Source);

        var response = await dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Lookup through changed server.", CorrelationId: "external-c2", ApproveRiskyTools: true),
            CancellationToken.None);

        Assert.Equal("Succeeded", response.Status);
        Assert.Equal(snapshotHash, Assert.Single(audit.Entries).SchemaVersion);
        Assert.Equal(snapshotHash, Assert.Single(server.Source.SnapshotTools).Definition.SchemaVersion);
        Assert.NotEqual(server.CurrentSchemaVersion(ExternalToolName), snapshotHash);
        Assert.True(model.SnapshotDefinitionWasObserved);
    }

    [Fact]
    public async Task HandleAsync_UnavailableExternalSourceDoesNotCrashLoop()
    {
        var unavailableSource = FakeExternalAgentToolSource.Unavailable();
        var audit = new CapturingToolAuditLogRepository();
        var dispatcher = CreateDispatcher(
            new SequenceModelClient([
                ToolResponse(ExternalToolName, """{"query":"A-100"}""")
            ]),
            audit,
            unavailableSource);

        var response = await dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Try unavailable external tool.", CorrelationId: "external-c2", ApproveRiskyTools: true),
            CancellationToken.None);

        Assert.Equal("ToolRejected", response.Status);
        var result = Assert.Single(response.ToolResults);
        Assert.Equal("UnknownTool", result.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.Rejected, result.ExecutionStatus);
        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("UnknownTool", auditEntry.PolicyDecision);
        Assert.Equal("Rejected", auditEntry.ExecutionStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_RealWrapperUnknownOutcomeStopsRemainingCallsAndAuditsMetadata(bool timeout)
    {
        const string secret = "private-remote-effect-and-argument";
        var client = FakeExternalMcpClient.WithTools(
            new ExternalMcpToolDescriptor("lookup", "Looks up an order.", Schema("query")),
            new ExternalMcpToolDescriptor("secondary", "Another operation.", Schema("query")));
        var remoteEffects = 0;
        client.CallOverride = async token =>
        {
            remoteEffects++;
            if (timeout)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            throw new InvalidOperationException(secret);
        };
        var factory = new SingleExternalMcpClientFactory(client);
        var manager = CreateRealManager(factory, timeout ? 0.01 : 30);
        await manager.RefreshAsync(CancellationToken.None);
        try
        {
            var source = new ExternalMcpAgentToolSource(manager, NullLoggerFactory.Instance);
            var hash = source.GetAvailableTools()[0].Definition.SchemaVersion;
            var audit = new CapturingToolAuditLogRepository();
            var model = new SequenceModelClient([ToolResponse(
                (ExternalToolName, $$"""{"query":"{{secret}}"}"""),
                ("mcp_orders_secondary", """{"query":"later"}"""))]);
            var dispatcher = CreateDispatcher(model, audit, source);

            var response = await dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
                new AgenticChatCommand("Use tools.", CorrelationId: "external-c2", ApproveRiskyTools: true),
                CancellationToken.None);

            Assert.Equal("ToolFailed", response.Status);
            Assert.Contains("did not confirm successful completion", response.Answer, StringComparison.Ordinal);
            Assert.Equal("mcp_tool_outcome_unknown", Assert.Single(response.ToolResults).ErrorCode);
            Assert.Equal(1, model.CallCount);
            Assert.Equal(1, client.CallCount);
            Assert.Equal(1, remoteEffects);
            Assert.Equal(1, factory.CreateCount);
            Assert.Equal(1, client.DisposeCount);
            Assert.Equal(2, audit.Entries.Count);
            AssertUnknownAudit(audit.Entries[0], hash);
            Assert.Equal(response.ConversationId, audit.Entries[0].ConversationId);
            Assert.Equal("NotExecuted", audit.Entries[1].ExecutionStatus);
            Assert.Equal("prior_tool_failed", audit.Entries[1].ErrorCode);
            AssertMetadataOnly(audit.Entries[1], hasOutput: false);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(audit.Entries), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(response), StringComparison.Ordinal);
        }
        finally
        {
            await new ExternalMcpHostedService(manager).DisposeAsync();
        }
    }

    [Fact]
    public async Task HandleAsync_RealWrapperCallerCancellationAuditsUnknownBeforePropagation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auditEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAudit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        var client = FakeExternalMcpClient.WithTools(
            new ExternalMcpToolDescriptor("lookup", "Looks up an order.", Schema("query")),
            new ExternalMcpToolDescriptor("secondary", "Another operation.", Schema("query")));
        client.CallOverride = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable private response");
        };
        var factory = new SingleExternalMcpClientFactory(client);
        var manager = CreateRealManager(factory);
        await manager.RefreshAsync(CancellationToken.None);
        try
        {
            var source = new ExternalMcpAgentToolSource(manager, NullLoggerFactory.Instance);
            var hash = source.GetAvailableTools()[0].Definition.SchemaVersion;
            var audit = new CapturingToolAuditLogRepository
            {
                AddOverride = async token =>
                {
                    Assert.True(caller.IsCancellationRequested);
                    Assert.False(token.CanBeCanceled);
                    auditEntered.SetResult();
                    await releaseAudit.Task;
                }
            };
            var model = new SequenceModelClient([ToolResponse(
                (ExternalToolName, """{"query":"private-argument"}"""),
                ("mcp_orders_secondary", """{"query":"later"}"""))]);
            var dispatcher = CreateDispatcher(model, audit, source);
            var dispatch = dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
                new AgenticChatCommand("Use tools.", CorrelationId: "external-c2", ApproveRiskyTools: true),
                caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            await caller.CancelAsync();
            await auditEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(dispatch.IsCompleted);
            Assert.Empty(audit.Entries);
            releaseAudit.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

            AssertUnknownAudit(Assert.Single(audit.Entries), hash);
            Assert.DoesNotContain("private-argument", JsonSerializer.Serialize(audit.Entries), StringComparison.Ordinal);
            Assert.Equal(1, model.CallCount);
            Assert.Equal(1, client.CallCount);
            Assert.Equal(1, factory.CreateCount);
            Assert.Equal(1, client.DisposeCount);
            Assert.Empty(source.GetAvailableTools());
        }
        finally
        {
            releaseAudit.TrySetResult();
            await new ExternalMcpHostedService(manager).DisposeAsync();
        }
    }

}
