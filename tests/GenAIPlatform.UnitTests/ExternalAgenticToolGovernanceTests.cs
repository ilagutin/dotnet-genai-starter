using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Agentic.Chat;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Infrastructure.Mcp;
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Observability.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed class ExternalAgenticToolGovernanceTests
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
            var source = new ExternalMcpAgentToolSource(manager);
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
            var source = new ExternalMcpAgentToolSource(manager);
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
            var source = new ExternalMcpAgentToolSource(manager);
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
            var source = new ExternalMcpAgentToolSource(manager);
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

    private static ExternalMcpConnectionManager CreateRealManager(IExternalMcpClientFactory factory, double timeout = 30)
    {
        return new ExternalMcpConnectionManager(
            Options.Create(new ExternalMcpOptions
            {
                RefreshInterval = TimeSpan.Zero,
                Servers = [new ExternalMcpServerOptions { Name = "Orders", Command = "fake", ToolCallTimeoutSeconds = timeout }]
            }),
            factory,
            new AlwaysConnectMcpPolicy(),
            NullLogger<ExternalMcpConnectionManager>.Instance);
    }

    private static void AssertUnknownAudit(ToolAuditLogEntry entry, string hash)
    {
        Assert.Equal("mcp_tool_outcome_unknown", entry.ErrorCode);
        Assert.Equal("Failed", entry.ExecutionStatus);
        Assert.Equal("alice", entry.UserId);
        Assert.Equal("tenant-a", entry.TenantId);
        Assert.Equal("external-c2", entry.CorrelationId);
        Assert.Equal("call-1", entry.ToolCallId);
        Assert.Equal(ExternalToolName, entry.ToolName);
        Assert.Equal(hash, entry.SchemaVersion);
        Assert.Equal("Valid", entry.ValidationStatus);
        Assert.Equal("RequiresApproval", entry.PolicyDecision);
        Assert.Equal("SimulatedApproved", entry.ApprovalState);
        AssertMetadataOnly(entry, hasOutput: false);
    }

    private static IApplicationDispatcher CreateDispatcher(
        IAiModelClient modelClient,
        IToolAuditLogRepository auditRepository,
        IExternalAgentToolSource externalSource,
        AgenticChatOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(new ConfigurationManager());
        services.AddSingleton(modelClient);
        services.AddSingleton(auditRepository);
        services.AddSingleton<IExternalAgentToolSource>(externalSource);
        services.AddSingleton<IUserContext>(new TestUserContext());
        services.AddSingleton<IPromptTemplateProvider>(new InMemoryPromptTemplateProvider());
        services.AddSingleton<IAiRequestLogRepository>(new CapturingAiRequestLogRepository());
        services.AddSingleton<IPricingRepository>(new EmptyPricingRepository());
        services.AddSingleton<ILogger<AiModelRequestLoggingService>>(
            NullLogger<AiModelRequestLoggingService>.Instance);
        services.AddSingleton<ILogger<AiRequestLogWriter>>(
            NullLogger<AiRequestLogWriter>.Instance);
        services.AddSingleton(Options.Create(new ModelGatewayOptions()));
        services.AddSingleton(Options.Create(options ?? new AgenticChatOptions()));

        return services
            .BuildServiceProvider()
            .GetRequiredService<IApplicationDispatcher>();
    }

    private static AiModelResponse ToolResponse(string toolName, string argumentsJson, string schemaVersion = "v1")
    {
        return ToolResponse((toolName, argumentsJson, schemaVersion));
    }

    private static AiModelResponse ToolResponse(params (string ToolName, string ArgumentsJson)[] toolCalls)
    {
        return ToolResponse(toolCalls.Select(static toolCall =>
            (toolCall.ToolName, toolCall.ArgumentsJson, SchemaVersion: "v1")).ToArray());
    }

    private static AiModelResponse ToolResponse(params (string ToolName, string ArgumentsJson, string SchemaVersion)[] toolCalls)
    {
        return new AiModelResponse(
            "External tool proposed.",
            "mock-chat",
            "mock",
            new AiModelUsage(5, 2, 7),
            "external-c2",
            toolCalls
                .Select((toolCall, index) => new AiToolCall(
                    $"call-{index + 1}",
                    toolCall.ToolName,
                    toolCall.SchemaVersion,
                    Json(toolCall.ArgumentsJson)))
                .ToArray());
    }

    private static AiModelResponse FinalResponse(string content)
    {
        return new AiModelResponse(
            content,
            "mock-chat",
            "mock",
            new AiModelUsage(3, 2, 5),
            "external-c2");
    }

    private static JsonElement Schema(string propertyName)
    {
        return Json($$"""
        {
          "type": "object",
          "properties": {
            "{{propertyName}}": { "type": "string" }
          },
          "required": [ "{{propertyName}}" ],
          "additionalProperties": false
        }
        """);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertMetadataOnly(ToolAuditLogEntry entry, bool hasOutput)
    {
        Assert.Equal(
            ["contentOmitted", "utf8Bytes"],
            entry.Arguments.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        Assert.Equal(hasOutput, entry.Output is not null);
        Assert.Null(entry.ErrorMessage);
        if (hasOutput)
        {
            Assert.Equal(
                ["contentOmitted", "returnedUtf8Bytes", "sourceUtf8Bytes", "truncated"],
                entry.Output!.Value.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        }
    }

    private static string SnapshotHash(
        string name,
        string description,
        JsonElement inputSchema)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', name, description, inputSchema.GetRawText()));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class FakeExternalMcpServer(Dictionary<string, FakeExternalMcpToolDescriptor> liveTools)
    {
        public FakeExternalAgentToolSource Source { get; private set; } = null!;

        public static FakeExternalMcpServer Connect(IReadOnlyList<FakeExternalMcpToolDescriptor> tools)
        {
            var server = new FakeExternalMcpServer(tools.ToDictionary(
                static tool => tool.Name,
                static tool => tool,
                StringComparer.Ordinal));
            server.Source = new FakeExternalAgentToolSource(server.SnapshotTools());
            return server;
        }

        public void MutateTool(
            string name,
            string description,
            JsonElement inputSchema)
        {
            liveTools[name] = liveTools[name] with
            {
                Description = description,
                InputSchema = inputSchema
            };
        }

        public string CurrentSchemaVersion(string name)
        {
            var tool = liveTools[name];
            return SnapshotHash(tool.Name, tool.Description, tool.InputSchema);
        }

        private IReadOnlyList<SnapshotExternalTool> SnapshotTools()
        {
            return liveTools.Values
                .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                .Select(tool => new SnapshotExternalTool(
                    tool.Name,
                    tool.Description,
                    SnapshotHash(tool.Name, tool.Description, tool.InputSchema),
                    tool.InputSchema,
                    tool.Policy,
                    this))
                .ToArray();
        }
    }

    private sealed class FakeExternalAgentToolSource(IReadOnlyList<SnapshotExternalTool> tools)
        : IExternalAgentToolSource
    {
        private readonly bool unavailable;

        private FakeExternalAgentToolSource()
            : this([])
        {
            unavailable = true;
        }

        public IReadOnlyList<SnapshotExternalTool> SnapshotTools { get; } = tools;

        public static FakeExternalAgentToolSource Unavailable()
        {
            return new FakeExternalAgentToolSource();
        }

        public IReadOnlyList<IAgentTool> GetAvailableTools()
        {
            return unavailable ? [] : SnapshotTools;
        }
    }

    private sealed record FakeExternalMcpToolDescriptor(
        string Name,
        string Description,
        JsonElement InputSchema,
        ToolPolicyMetadata Policy)
    {
        public static FakeExternalMcpToolDescriptor RequiresApproval(
            string name,
            string description,
            JsonElement inputSchema)
        {
            return new FakeExternalMcpToolDescriptor(
                name,
                description,
                inputSchema,
                ToolPolicyMetadata.ApprovalRequired("External MCP tools require approval by default."));
        }

        public static FakeExternalMcpToolDescriptor Forbidden(
            string name,
            string description,
            JsonElement inputSchema)
        {
            return new FakeExternalMcpToolDescriptor(
                name,
                description,
                inputSchema,
                new ToolPolicyMetadata(
                    ToolRisk.Forbidden,
                    "Forbidden",
                    "The backend policy forbids this external MCP tool.",
                    RequiresApproval: false,
                    MayExecute: false));
        }
    }

    private sealed class SnapshotExternalTool(
        string name,
        string description,
        string snapshotHash,
        JsonElement inputSchema,
        ToolPolicyMetadata policy,
        FakeExternalMcpServer server) : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(name, description, snapshotHash, inputSchema);

        public ToolPolicyMetadata Policy { get; } = policy;

        public ToolAuditContentPolicy AuditContentPolicy => ToolAuditContentPolicy.MetadataOnly;

        public int ValidateCalls { get; private set; }

        public int ExecuteCalls { get; private set; }

        public ToolValidationResult Validate(JsonElement arguments)
        {
            ValidateCalls++;
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("query", out var query) ||
                query.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(query.GetString()))
            {
                return ToolValidationResult.Invalid(
                    "missing_required_argument",
                    "External MCP test tool requires a non-empty query argument.");
            }

            return ToolValidationResult.Valid(JsonSerializer.SerializeToElement(new
            {
                query = query.GetString()
            }));
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            var output = JsonSerializer.SerializeToElement(new
            {
                ok = true,
                query = sanitizedArguments.GetProperty("query").GetString(),
                liveSchemaVersion = server.CurrentSchemaVersion(Definition.Name)
            });
            var outputBytes = Encoding.UTF8.GetByteCount(output.GetRawText());
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                output,
                PayloadMetadata: new ToolExecutionPayloadMetadata(outputBytes, outputBytes, false)));
        }
    }

    private sealed class SequenceModelClient(Queue<AiModelResponse> responses) : IAiModelClient
    {
        public int CallCount { get; private set; }

        public SequenceModelClient(IEnumerable<AiModelResponse> responses)
            : this(new Queue<AiModelResponse>(responses))
        {
        }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responses.Count == 0
                ? FinalResponse("Done.")
                : responses.Dequeue());
        }
    }

    private sealed class SnapshotProbeModelClient(
        string toolName,
        string expectedSchemaVersion,
        string expectedDescription) : IAiModelClient
    {
        private int calls;

        public bool SnapshotDefinitionWasObserved { get; private set; }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            calls++;
            if (calls == 1)
            {
                var definition = Assert.Single(request.Tools ?? [], tool => tool.Name == toolName);
                Assert.Equal(expectedSchemaVersion, definition.SchemaVersion);
                Assert.Equal(expectedDescription, definition.Description);
                SnapshotDefinitionWasObserved = true;
                return Task.FromResult(ToolResponse(toolName, """{"query":"A-100"}""", "model-mutated-v9"));
            }

            return Task.FromResult(FinalResponse("Rug-pull checked."));
        }
    }

    private sealed class SingleExternalMcpClientFactory(IExternalMcpClient client) : IExternalMcpClientFactory
    {
        public int CreateCount { get; private set; }

        public Task<IExternalMcpClient> CreateAsync(
            ExternalMcpServerOptions server,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromResult(client);
        }
    }

    private sealed class FakeExternalMcpClient(IReadOnlyList<ExternalMcpToolDescriptor> tools) : IExternalMcpClient
    {
        public int CallCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Func<CancellationToken, Task<ExternalMcpToolCallResult>>? CallOverride { get; set; }

        public static FakeExternalMcpClient WithTools(params ExternalMcpToolDescriptor[] tools)
        {
            return new FakeExternalMcpClient(tools);
        }

        public Task<IReadOnlyList<ExternalMcpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(tools);
        }

        public Task<ExternalMcpToolCallResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return CallOverride?.Invoke(cancellationToken) ?? Task.FromResult(new ExternalMcpToolCallResult(
                IsError: false,
                JsonSerializer.SerializeToElement(new { ok = true }),
                ErrorMessage: null));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
    private sealed class CapturingToolAuditLogRepository : IToolAuditLogRepository
    {
        public List<ToolAuditLogEntry> Entries { get; } = [];

        public Func<CancellationToken, Task>? AddOverride { get; init; }

        public async Task AddAsync(ToolAuditLogEntry entry, CancellationToken cancellationToken)
        {
            if (AddOverride is not null)
            {
                await AddOverride(cancellationToken);
            }

            Entries.Add(entry);
        }
    }

    private sealed class CapturingAiRequestLogRepository : IAiRequestLogRepository
    {
        public Task AddAsync(AiRequestLogEntry entry, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyPricingRepository : IPricingRepository
    {
        public Task<PricingRecord?> GetEffectivePricingAsync(
            string provider,
            string model,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<PricingRecord?>(null);
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
}
