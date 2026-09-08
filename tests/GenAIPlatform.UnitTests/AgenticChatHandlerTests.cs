using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Agentic.Chat;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.UnitTests;

public sealed partial class AgenticChatHandlerTests
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
        Assert.Equal("schema_invalid", result.ErrorCode);
        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal("Invalid", auditEntry.ValidationStatus);
        Assert.Equal("Allowed", auditEntry.PolicyDecision);
        Assert.Equal("ValidationFailed", auditEntry.ExecutionStatus);
        Assert.Equal("schema_invalid", auditEntry.ErrorCode);
        Assert.Null(auditEntry.Output);
    }

    [Fact]
    public async Task HandleAsync_SchemaInvalidPayloadDoesNotExposeArgumentNamesOrValues()
    {
        const string secretKey = "syntheticSecretKey";
        const string secretValue = "synthetic-secret-value";
        var audit = new CapturingToolAuditLogRepository();
        var logger = new TestLogger<AgentToolExecutor>();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTool("GetCurrentUserProfile", $$"""{"{{secretKey}}":"{{secretValue}}"}""")
            ]),
            audit,
            toolExecutorLogger: logger);

        var response = await handler.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand("Use my profile.", CorrelationId: "agent-schema-secret"),
            CancellationToken.None);

        var result = Assert.Single(response.ToolResults);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("schema_invalid", result.ErrorCode);
        Assert.Equal(ToolExecutionStatus.ValidationFailed, result.ExecutionStatus);
        Assert.Equal("Invalid", entry.ValidationStatus);
        Assert.Equal("ValidationFailed", entry.ExecutionStatus);
        Assert.Equal("{}", entry.Arguments.GetRawText());
        Assert.Null(entry.Output);
        Assert.InRange(entry.ErrorMessage!.Length, 1, 256);
        var exposedText = string.Join('|', new[]
        {
            result.ErrorCode,
            result.Result,
            entry.ErrorCode,
            entry.ErrorMessage,
            entry.Arguments.GetRawText(),
            entry.Output?.GetRawText()
        }.Concat(logger.Entries.Select(static log => log.Message)));
        Assert.DoesNotContain(secretKey, exposedText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, exposedText, StringComparison.Ordinal);
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
        var logger = new TestLogger<AgentToolExecutor>();
        var handler = CreateHandler(
            new SequenceModelClient([
                ResponseWithTools(
                    ("call-1", "DeleteDocument", """{"documentId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}"""),
                    ("call-2", "GetCurrentUserProfile", """{"syntheticSecretKey":"synthetic-secret-value"}"""))
            ]),
            audit,
            toolExecutorLogger: logger);

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
        Assert.Equal("Invalid", audit.Entries[1].ValidationStatus);
        Assert.Equal("ValidationFailed", audit.Entries[1].ExecutionStatus);
        Assert.Equal("schema_invalid", audit.Entries[1].ErrorCode);
        Assert.Equal("{}", audit.Entries[1].Arguments.GetRawText());
        Assert.Null(audit.Entries[1].Output);
        var exposedText = string.Join('|',
            audit.Entries.SelectMany(static entry => new[]
            {
                entry.ErrorCode,
                entry.ErrorMessage,
                entry.Arguments.GetRawText(),
                entry.Output?.GetRawText()
            }).Concat(response.ToolResults.SelectMany(static result => new[]
            {
                result.ErrorCode,
                result.Result
            })).Concat(logger.Entries.Select(static entry => entry.Message)));
        Assert.DoesNotContain("syntheticSecretKey", exposedText, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-value", exposedText, StringComparison.Ordinal);
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
            options: new AgenticChatOptions { MaxEstimatedCost = 0.1m },
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

}
