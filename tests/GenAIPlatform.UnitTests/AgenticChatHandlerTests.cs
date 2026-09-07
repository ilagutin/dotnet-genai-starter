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
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Observability.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed class AgenticChatHandlerTests
{
    [Fact]
    public async Task HandleAsync_ExecutesSafeToolAfterBackendPolicyApproval()
    {
        var audit = new CapturingToolAuditLogRepository();
        var aiLogs = new CapturingAiRequestLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTool("GetCurrentUserProfile", "{}"),
                new AiModelResponse("Done with profile.", "mock-chat", "mock", new AiModelUsage(10, 4, 14), "agent-test")
            ]),
            audit,
            aiLogRepository: aiLogs);

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use my profile.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("Succeeded", response.Status);
        Assert.Equal(1, response.ToolCalls);
        var result = Assert.Single(response.ToolResults);
        Assert.Equal("Allowed", result.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.Succeeded, result.ExecutionStatus);
        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("Valid", auditEntry.ValidationStatus);
        Assert.Equal("Allowed", auditEntry.PolicyDecision);
        Assert.Equal("Succeeded", auditEntry.ExecutionStatus);
        Assert.Equal(2, aiLogs.Entries.Count);
        Assert.All(aiLogs.Entries, entry =>
        {
            Assert.Equal("agentic-chat", entry.Prompt?.TemplateName);
            Assert.Equal("agent-test", entry.CorrelationId);
        });
    }

    [Fact]
    public async Task HandleAsync_ExecutesRegisteredToolUsingToolMetadata()
    {
        var audit = new CapturingToolAuditLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTool("CustomLookup", "{}"),
                new AiModelResponse("Custom lookup complete.", "mock-chat", "mock", new AiModelUsage(5, 2, 7), "agent-test")
            ]),
            audit,
            toolRegistry: new StaticToolRegistry([new CustomLookupTool()]));

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use the custom lookup.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("Succeeded", response.Status);
        var result = Assert.Single(response.ToolResults);
        Assert.Equal("CustomLookup", result.ToolName);
        Assert.Equal("Allowed", result.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.Succeeded, result.ExecutionStatus);
        Assert.Equal("Allowed", Assert.Single(audit.Entries).PolicyDecision);
    }

    [Fact]
    public async Task HandleAsync_RejectsMalformedNoArgumentToolCallBeforeExecution()
    {
        var audit = new CapturingToolAuditLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithInvalidToolArguments("GetCurrentUserProfile", "not-json")
            ]),
            audit);

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use my profile.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("ToolRejected", response.Status);
        Assert.Equal(1, response.ToolCalls);
        var result = Assert.Single(response.ToolResults);
        Assert.Equal("GetCurrentUserProfile", result.ToolName);
        Assert.Equal("Allowed", result.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.ValidationFailed, result.ExecutionStatus);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("Invalid", auditEntry.ValidationStatus);
        Assert.Equal("Allowed", auditEntry.PolicyDecision);
        Assert.Equal("ValidationFailed", auditEntry.ExecutionStatus);
        Assert.Equal("invalid_arguments", auditEntry.ErrorCode);
        Assert.Null(auditEntry.Output);
    }

    [Fact]
    public async Task HandleAsync_RejectsUnknownToolBeforeExecution()
    {
        var audit = new CapturingToolAuditLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTool("UnregisteredTool", "{}")
            ]),
            audit);

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use an unavailable tool.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("ToolRejected", response.Status);
        var result = Assert.Single(response.ToolResults);
        Assert.Equal("UnregisteredTool", result.ToolName);
        Assert.Equal("UnknownTool", result.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.Rejected, result.ExecutionStatus);

        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("Invalid", auditEntry.ValidationStatus);
        Assert.Equal("UnknownTool", auditEntry.PolicyDecision);
        Assert.Equal("Rejected", auditEntry.ExecutionStatus);
    }

    [Fact]
    public async Task HandleAsync_RequiresSimulatedApprovalForRiskyTool()
    {
        var audit = new CapturingToolAuditLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTool("DraftEmail", """{"to":"a@example.test","subject":"Hello","body":"Draft only"}""")
            ]),
            audit);

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Draft email.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("ApprovalRequired", response.Status);
        var result = Assert.Single(response.ToolResults);
        Assert.Equal("RequiresApproval", result.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.ApprovalRequired, result.ExecutionStatus);
        Assert.Equal("Required", Assert.Single(audit.Entries).ApprovalState);
    }

    [Fact]
    public async Task HandleAsync_ExecutesRiskyToolOnlyAfterSimulatedApproval()
    {
        var audit = new CapturingToolAuditLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTool("DraftEmail", """{"to":"a@example.test","subject":"Hello","body":"Draft only"}"""),
                new AiModelResponse("Draft created.", "mock-chat", "mock", new AiModelUsage(5, 2, 7), "agent-test")
            ]),
            audit);

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Draft email.", CorrelationId: "agent-test", ApproveRiskyTools: true),
            CancellationToken.None);

        Assert.Equal("Succeeded", response.Status);
        var result = Assert.Single(response.ToolResults);
        Assert.Equal("RequiresApproval", result.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.Succeeded, result.ExecutionStatus);
        Assert.Equal("SimulatedApproved", Assert.Single(audit.Entries).ApprovalState);
    }

    [Fact]
    public async Task HandleAsync_RejectsForbiddenToolBeforeExecution()
    {
        var audit = new CapturingToolAuditLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTools(
                    ("call-1", "DeleteDocument", """{"documentId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}"""),
                    ("call-2", "GetCurrentUserProfile", "{}"))
            ]),
            audit);

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Delete a document.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("ToolRejected", response.Status);
        Assert.Equal(2, response.ToolCalls);
        var result = Assert.Single(response.ToolResults);
        Assert.Equal("Forbidden", result.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.Rejected, result.ExecutionStatus);
        Assert.Equal("tool_forbidden", result.ErrorCode);
        Assert.Equal(2, audit.Entries.Count);
        Assert.Equal("call-1", audit.Entries[0].ToolCallId);
        Assert.Equal("Rejected", audit.Entries[0].ExecutionStatus);
        Assert.Equal("call-2", audit.Entries[1].ToolCallId);
        Assert.Equal("NotExecuted", audit.Entries[1].ExecutionStatus);
        Assert.Equal("prior_tool_rejected", audit.Entries[1].ErrorCode);
    }

    [Fact]
    public async Task HandleAsync_AuditsToolCallThatExceedsLimit()
    {
        var audit = new CapturingToolAuditLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTools(
                    ("call-1", "GetCurrentUserProfile", "{}"),
                    ("call-2", "GetCurrentUserProfile", "{}"))
            ]),
            audit,
            new AgenticChatOptions
            {
                MaxSteps = 4,
                TimeoutSeconds = 15,
                MaxToolCalls = 1,
                MaxTotalTokens = 4096,
                MaxEstimatedCost = 1,
                EstimatedCostPerThousandTokens = 0,
                PolicyVersion = "tool-policy-v1"
            });

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use profile twice.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("ToolLimitExceeded", response.Status);
        Assert.Equal(2, response.ToolCalls);
        Assert.Equal(2, audit.Entries.Count);
        var overLimitEntry = audit.Entries[1];
        Assert.Equal("call-2", overLimitEntry.ToolCallId);
        Assert.Equal("Rejected", overLimitEntry.ExecutionStatus);
        Assert.Equal("tool_limit_exceeded", overLimitEntry.ErrorCode);
        Assert.Null(overLimitEntry.Output);
    }

    [Fact]
    public async Task HandleAsync_AuditsToolExecutionFailureAndRemainingProposals()
    {
        var audit = new CapturingToolAuditLogRepository();
        var userContext = new TestUserContext();
        var toolExecutorLogger = new TestLogger<AgentToolExecutor>();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTools(
                    ("call-1", "CreateSupportTicket", "{}"),
                    ("call-2", "GetCurrentUserProfile", "{}"))
            ]),
            audit,
            toolRegistry: new StaticToolRegistry([
                new ThrowingDemoTool(),
                new GetCurrentUserProfileTestTool(userContext)
            ]),
            userContext: userContext,
            toolExecutorLogger: toolExecutorLogger);

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Run failing tool.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("ToolFailed", response.Status);
        Assert.Equal(2, response.ToolCalls);
        Assert.Equal(2, audit.Entries.Count);
        Assert.Equal("call-1", audit.Entries[0].ToolCallId);
        Assert.Equal("Failed", audit.Entries[0].ExecutionStatus);
        Assert.Equal("tool_execution_failed", audit.Entries[0].ErrorCode);
        Assert.Equal("call-2", audit.Entries[1].ToolCallId);
        Assert.Equal("NotExecuted", audit.Entries[1].ExecutionStatus);
        Assert.Equal("prior_tool_failed", audit.Entries[1].ErrorCode);
        var log = Assert.Single(toolExecutorLogger.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.IsType<InvalidOperationException>(log.Exception);
        Assert.Contains("CreateSupportTicket", log.Message, StringComparison.Ordinal);
        Assert.Contains("call-1", log.Message, StringComparison.Ordinal);
        Assert.Contains("agent-test", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_DiagnosesPipelineOnlyStatusReturnedByToolImplementation()
    {
        var audit = new CapturingToolAuditLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTool("CreateSupportTicket", "{}")
            ]),
            audit,
            toolRegistry: new StaticToolRegistry([
                new UnexpectedStatusDemoTool(ToolExecutionStatus.Rejected)
            ]));

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Run misconfigured tool.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("ToolFailed", response.Status);
        var result = Assert.Single(response.ToolResults);
        Assert.Equal("CreateSupportTicket", result.ToolName);
        Assert.Equal("Allowed", result.PolicyDecision);
        Assert.Equal(ToolExecutionStatus.Failed, result.ExecutionStatus);
        Assert.Equal("tool_unexpected_execution_status", result.ErrorCode);

        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("Valid", auditEntry.ValidationStatus);
        Assert.Equal("Allowed", auditEntry.PolicyDecision);
        Assert.Equal("NotRequired", auditEntry.ApprovalState);
        Assert.Equal("Failed", auditEntry.ExecutionStatus);
        Assert.Equal("tool_unexpected_execution_status", auditEntry.ErrorCode);
        Assert.Contains("Rejected", auditEntry.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(auditEntry.Output);
    }

    [Fact]
    public async Task HandleAsync_AuditsToolExecutionCancellationAsFailedResult()
    {
        var audit = new CapturingToolAuditLogRepository();
        var userContext = new TestUserContext();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTool("CreateSupportTicket", "{}")
            ]),
            audit,
            toolRegistry: new StaticToolRegistry([new CancelingDemoTool()]),
            userContext: userContext);

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Run canceling tool.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("ToolFailed", response.Status);
        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("Failed", auditEntry.ExecutionStatus);
        Assert.Equal("tool_execution_canceled", auditEntry.ErrorCode);
    }

    [Fact]
    public async Task HandleAsync_StopsWhenModelCallExceedsTimeout()
    {
        var handler = CreateHandler(
            new DelayingModelClient(),
            new CapturingToolAuditLogRepository(),
            new AgenticChatOptions
            {
                MaxSteps = 4,
                TimeoutSeconds = 1,
                MaxToolCalls = 4,
                MaxTotalTokens = 4096,
                MaxEstimatedCost = 1,
                EstimatedCostPerThousandTokens = 0,
                PolicyVersion = "tool-policy-v1"
            });

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Wait too long.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("TimedOut", response.Status);
        Assert.Equal(0, response.ToolCalls);
    }

    [Fact]
    public async Task HandleAsync_StopsAtStepLimit()
    {
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTool("GetCurrentUserProfile", "{}"),
                ResponseWithTool("GetCurrentUserProfile", "{}")
            ]),
            new CapturingToolAuditLogRepository(),
            new AgenticChatOptions
            {
                MaxSteps = 1,
                TimeoutSeconds = 15,
                MaxToolCalls = 4,
                MaxTotalTokens = 4096,
                MaxEstimatedCost = 1,
                EstimatedCostPerThousandTokens = 0,
                PolicyVersion = "tool-policy-v1"
            });

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use profile repeatedly.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("StepLimitExceeded", response.Status);
    }

    [Fact]
    public async Task HandleAsync_StopsAtTokenBudget()
    {
        var audit = new CapturingToolAuditLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                new AiModelResponse(
                    "Large response.",
                    "mock-chat",
                    "mock",
                    new AiModelUsage(100, 100, 200),
                    "agent-test",
                    [new AiToolCall("call-1", "GetCurrentUserProfile", "v1", EmptyArguments())])
            ]),
            audit,
            new AgenticChatOptions
            {
                MaxSteps = 4,
                TimeoutSeconds = 15,
                MaxToolCalls = 4,
                MaxTotalTokens = 100,
                MaxEstimatedCost = 1,
                EstimatedCostPerThousandTokens = 0,
                PolicyVersion = "tool-policy-v1"
            });

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Spend too much.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("BudgetExceeded", response.Status);
        Assert.Equal(1, response.ToolCalls);
        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("call-1", auditEntry.ToolCallId);
        Assert.Equal("NotExecuted", auditEntry.ExecutionStatus);
        Assert.Equal("budget_exceeded", auditEntry.ErrorCode);
    }

    [Fact]
    public async Task HandleAsync_UsesPricingRecordsForEstimatedCostAndTelemetry()
    {
        var aiLogs = new CapturingAiRequestLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                new AiModelResponse(
                    "Priced response.",
                    "mock-chat",
                    "mock",
                    new AiModelUsage(1_000, 2_000, 3_000),
                    "agent-test")
            ]),
            new CapturingToolAuditLogRepository(),
            aiLogRepository: aiLogs,
            pricingRepository: new InMemoryPricingRepository([
                CreatePricingRecord(
                    "mock",
                    "mock-chat",
                    inputTokenPricePerMillion: 10m,
                    outputTokenPricePerMillion: 20m)
            ]));

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Estimate this.", CorrelationId: "agent-test"),
            CancellationToken.None);

        var aiLog = Assert.Single(aiLogs.Entries);
        Assert.Equal("Succeeded", response.Status);
        Assert.Equal(0.05000000m, response.EstimatedCost);
        Assert.Equal(response.EstimatedCost, aiLog.EstimatedCost);
    }

    [Fact]
    public async Task HandleAsync_StopsAtCostBudgetUsingPricingRecord()
    {
        var audit = new CapturingToolAuditLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                new AiModelResponse(
                    "Costly tool proposal.",
                    "mock-chat",
                    "mock",
                    new AiModelUsage(1_000_000, 0, 1_000_000),
                    "agent-test",
                    [new AiToolCall("call-1", "GetCurrentUserProfile", "v1", EmptyArguments())])
            ]),
            audit,
            new AgenticChatOptions
            {
                MaxSteps = 4,
                TimeoutSeconds = 15,
                MaxToolCalls = 4,
                MaxTotalTokens = 2_000_000,
                MaxEstimatedCost = 1,
                EstimatedCostPerThousandTokens = 0,
                PolicyVersion = "tool-policy-v1"
            },
            pricingRepository: new InMemoryPricingRepository([
                CreatePricingRecord(
                    "mock",
                    "mock-chat",
                    inputTokenPricePerMillion: 2m,
                    outputTokenPricePerMillion: 0m)
            ]));

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Spend too much.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("BudgetExceeded", response.Status);
        Assert.Equal(2.00000000m, response.EstimatedCost);
        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("NotExecuted", auditEntry.ExecutionStatus);
        Assert.Equal("budget_exceeded", auditEntry.ErrorCode);
    }

    [Fact]
    public async Task HandleAsync_FallsBackToConfiguredCostWhenPricingRecordIsUnavailable()
    {
        var aiLogs = new CapturingAiRequestLogRepository();
        var handler = CreateHandler(
            new SequenceModelClient([
                new AiModelResponse(
                    "Fallback-priced tool proposal.",
                    "mock-chat",
                    "mock",
                    new AiModelUsage(500, 500, 1_000),
                    "agent-test",
                    [new AiToolCall("call-1", "GetCurrentUserProfile", "v1", EmptyArguments())])
            ]),
            new CapturingToolAuditLogRepository(),
            new AgenticChatOptions
            {
                MaxSteps = 4,
                TimeoutSeconds = 15,
                MaxToolCalls = 4,
                MaxTotalTokens = 4096,
                MaxEstimatedCost = 0.005m,
                EstimatedCostPerThousandTokens = 0.01m,
                PolicyVersion = "tool-policy-v1"
            },
            aiLogRepository: aiLogs);

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use fallback pricing.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("BudgetExceeded", response.Status);
        Assert.Equal(0.01000000m, response.EstimatedCost);
        Assert.Null(Assert.Single(aiLogs.Entries).EstimatedCost);
    }

    [Fact]
    public async Task HandleAsync_PassesReadOnlyGrowingMessagesAcrossToolSteps()
    {
        var modelClient = new ReadOnlyMessagesProbeModelClient();
        var handler = CreateHandler(
            modelClient,
            new CapturingToolAuditLogRepository());

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use my profile.", CorrelationId: "agent-test"),
            CancellationToken.None);

        Assert.Equal("Succeeded", response.Status);
        Assert.NotNull(modelClient.SecondCallMessages);
        var secondCallMessages = modelClient.SecondCallMessages;
        var assistantIndex = Array.FindIndex(
            secondCallMessages.ToArray(),
            static message => message.Role == AiMessageRole.Assistant &&
                              message.ToolCalls is { Count: > 0 });
        Assert.True(assistantIndex >= 0);
        var proposedToolCall = Assert.Single(secondCallMessages[assistantIndex].ToolCalls ?? []);
        Assert.Equal("call-1", proposedToolCall.Id);
        Assert.Equal("GetCurrentUserProfile", proposedToolCall.Name);

        Assert.True(assistantIndex + 1 < secondCallMessages.Count);
        var toolResultMessage = secondCallMessages[assistantIndex + 1];
        Assert.Equal(AiMessageRole.Tool, toolResultMessage.Role);
        Assert.Equal("call-1", toolResultMessage.ToolCallId);
        Assert.Contains("alice", toolResultMessage.Content, StringComparison.Ordinal);
    }

    private static IApplicationDispatcher CreateHandler(
        IAiModelClient modelClient,
        IToolAuditLogRepository auditRepository,
        AgenticChatOptions? options = null,
        CapturingAiRequestLogRepository? aiLogRepository = null,
        IPricingRepository? pricingRepository = null,
        IAgentToolRegistry? toolRegistry = null,
        TestUserContext? userContext = null,
        TestLogger<AgentToolExecutor>? toolExecutorLogger = null)
    {
        userContext ??= new TestUserContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(new Microsoft.Extensions.Configuration.ConfigurationManager());
        services.AddSingleton<IAiModelClient>(modelClient);
        services.AddSingleton<IToolAuditLogRepository>(auditRepository);
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton<IPromptTemplateProvider>(new InMemoryPromptTemplateProvider());
        services.AddSingleton<IAiRequestLogRepository>(
            aiLogRepository ?? new CapturingAiRequestLogRepository());
        services.AddSingleton<IPricingRepository>(pricingRepository ?? new EmptyPricingRepository());
        services.AddSingleton<ILogger<AiModelRequestLoggingService>>(
            NullLogger<AiModelRequestLoggingService>.Instance);
        services.AddSingleton<ILogger<AiRequestLogWriter>>(
            NullLogger<AiRequestLogWriter>.Instance);
        services.AddSingleton<ILogger<AgentToolExecutor>>(
            toolExecutorLogger is null
                ? NullLogger<AgentToolExecutor>.Instance
                : toolExecutorLogger);
        services.AddSingleton(Options.Create(new ModelGatewayOptions()));
        services.AddSingleton(Options.Create(options ?? new AgenticChatOptions()));
        if (toolRegistry is not null)
        {
            services.AddSingleton<IAgentToolRegistry>(toolRegistry);
        }

        return services
            .BuildServiceProvider()
            .GetRequiredService<IApplicationDispatcher>();
    }

    private static AiModelResponse ResponseWithTool(string toolName, string argumentsJson)
    {
        using var arguments = JsonDocument.Parse(argumentsJson);
        return new AiModelResponse(
            "Tool proposed.",
            "mock-chat",
            "mock",
            new AiModelUsage(5, 2, 7),
            "agent-test",
            [new AiToolCall("call-1", toolName, "v1", arguments.RootElement.Clone())]);
    }

    private static AiModelResponse ResponseWithTools(params (string Id, string Name, string ArgumentsJson)[] toolCalls)
    {
        var calls = toolCalls
            .Select(static toolCall =>
            {
                using var arguments = JsonDocument.Parse(toolCall.ArgumentsJson);
                return new AiToolCall(toolCall.Id, toolCall.Name, "v1", arguments.RootElement.Clone());
            })
            .ToArray();

        return new AiModelResponse(
            "Tools proposed.",
            "mock-chat",
            "mock",
            new AiModelUsage(5, 2, 7),
            "agent-test",
            calls);
    }

    private static AiModelResponse ResponseWithInvalidToolArguments(string toolName, string argumentsText)
    {
        return new AiModelResponse(
            "Tool proposed.",
            "mock-chat",
            "mock",
            new AiModelUsage(5, 2, 7),
            "agent-test",
            [new AiToolCall("call-1", toolName, "v1", JsonSerializer.SerializeToElement(argumentsText))]);
    }

    private static JsonElement EmptyArguments()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static PricingRecord CreatePricingRecord(
        string provider,
        string model,
        decimal inputTokenPricePerMillion,
        decimal outputTokenPricePerMillion)
    {
        return new PricingRecord(
            Guid.NewGuid(),
            provider,
            model,
            "USD",
            inputTokenPricePerMillion,
            outputTokenPricePerMillion,
            EmbeddingTokenPricePerMillion: null,
            DateTimeOffset.Parse("2000-01-01T00:00:00Z"),
            EffectiveToUtc: null);
    }

    private sealed class SequenceModelClient(Queue<AiModelResponse> responses) : IAiModelClient
    {
        public SequenceModelClient(IEnumerable<AiModelResponse> responses)
            : this(new Queue<AiModelResponse>(responses))
        {
        }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responses.Count == 0
                ? new AiModelResponse("Done.", request.Model, "mock", new AiModelUsage(1, 1, 2), request.CorrelationId)
                : responses.Dequeue());
        }
    }

    private sealed class DelayingModelClient : IAiModelClient
    {
        public async Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new AiModelResponse(
                "Too late.",
                request.Model,
                "mock",
                new AiModelUsage(1, 1, 2),
                request.CorrelationId);
        }
    }

    private sealed class ReadOnlyMessagesProbeModelClient : IAiModelClient
    {
        private int calls;

        public IReadOnlyList<AiChatMessage>? SecondCallMessages { get; private set; }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            calls++;
            if (calls == 1)
            {
                Assert.IsNotType<List<AiChatMessage>>(request.Messages);
                var collection = Assert.IsAssignableFrom<ICollection<AiChatMessage>>(request.Messages);
                Assert.True(collection.IsReadOnly);
                Assert.Throws<NotSupportedException>(() =>
                    collection.Add(new AiChatMessage(AiMessageRole.User, "external mutation")));

                return Task.FromResult(ResponseWithTool("GetCurrentUserProfile", "{}"));
            }

            SecondCallMessages = request.Messages.ToArray();
            return Task.FromResult(new AiModelResponse(
                "Done after tool.",
                request.Model,
                "mock",
                new AiModelUsage(1, 1, 2),
                request.CorrelationId));
        }
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

    private sealed class CapturingAiRequestLogRepository : IAiRequestLogRepository
    {
        public List<AiRequestLogEntry> Entries { get; } = [];

        public Task AddAsync(AiRequestLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
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

    private sealed class InMemoryPricingRepository(IReadOnlyList<PricingRecord> records) : IPricingRepository
    {
        public Task<PricingRecord?> GetEffectivePricingAsync(
            string provider,
            string model,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            var record = records
                .Where(current =>
                    current.Provider == provider &&
                    current.Model == model &&
                    current.EffectiveFromUtc <= usedAtUtc &&
                    (current.EffectiveToUtc is null || current.EffectiveToUtc > usedAtUtc))
                .OrderByDescending(static current => current.EffectiveFromUtc)
                .FirstOrDefault();

            return Task.FromResult(record);
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

    private sealed class StaticToolRegistry(IReadOnlyList<IAgentTool> tools) : IAgentToolRegistry
    {
        public IReadOnlyList<IAgentTool> GetAvailableTools()
        {
            return tools;
        }
    }

    private sealed class ThrowingDemoTool : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "CreateSupportTicket",
            "Throws during execution for bounded failure coverage.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Creates an idempotent demo support ticket record.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "CreateSupportTicket expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class CancelingDemoTool : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "CreateSupportTicket",
            "Cancels during execution for bounded cancellation coverage.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Creates an idempotent demo support ticket record.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "CreateSupportTicket expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException();
        }
    }

    private sealed class UnexpectedStatusDemoTool(ToolExecutionStatus status) : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "CreateSupportTicket",
            "Returns a pipeline-only status for contract diagnostics.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Creates an idempotent demo support ticket record.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "CreateSupportTicket expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(
                status,
                JsonSerializer.SerializeToElement(new { ignored = true }),
                "tool_claimed_pipeline_status",
                "Tool attempted to return a pipeline-only status."));
        }
    }

    private sealed class GetCurrentUserProfileTestTool(IUserContext userContext) : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "GetCurrentUserProfile",
            "Returns the current demo user's id.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Read-only demo profile lookup.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "GetCurrentUserProfile expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                JsonSerializer.SerializeToElement(new { userContext.UserId })));
        }
    }

    private sealed class CustomLookupTool : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "CustomLookup",
            "Test-only lookup tool registered outside the demo registry.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Custom lookup is safe for this request.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "CustomLookup expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                JsonSerializer.SerializeToElement(new { value = "custom" })));
        }
    }
}
