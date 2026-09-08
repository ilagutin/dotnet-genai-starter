using System.Text.Json;
using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Agentic.Chat;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Domain.Prompts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GenAIPlatform.UnitTests;

public sealed class AgenticUsageBoundaryTests
{
    public static TheoryData<AiModelUsage?, string, string> UnusableUsage => new()
    {
        { null, "UsageUnavailable", "usage_unavailable" },
        { new(null, null, null), "UsageUnavailable", "usage_unavailable" },
        { new(-1, 2, 1), "InvalidUsage", "invalid_usage" },
        { new(2, -1, 1), "InvalidUsage", "invalid_usage" },
        { new(null, null, -1), "InvalidUsage", "invalid_usage" },
        { new(1, null, null), "InvalidUsage", "invalid_usage" },
        { new(null, 1, null), "InvalidUsage", "invalid_usage" },
        { new(1, 2, 4), "InvalidUsage", "invalid_usage" },
        { new(int.MaxValue, 1, null), "InvalidUsage", "invalid_usage" },
        { new(int.MaxValue, 1, int.MaxValue), "InvalidUsage", "invalid_usage" }
    };

    [Theory]
    [MemberData(nameof(UnusableUsage))]
    public async Task UnusableResponseStopsBeforeEstimatorToolsOrNextModel(
        AiModelUsage? usage, string status, string code)
    {
        using var fixture = new Fixture([Response(usage, tool: true)]);
        var result = await fixture.RunAsync();

        Assert.Equal(status, result.Status);
        Assert.Equal(1, fixture.Model.Calls);
        Assert.Equal(0, fixture.Estimator.Calls);
        Assert.Equal(0, fixture.Tool.Calls);
        Assert.Equal(0, result.TotalTokens);
        Assert.Equal(0m, result.EstimatedCost);
        Assert.Null(result.Usage);
        AssertSkipped(Assert.Single(fixture.Audit.Entries), code);
    }

    [Theory]
    [MemberData(nameof(UnusableUsage))]
    public async Task LaterUnusableResponsePreservesPriorMeasuredAccounting(
        AiModelUsage? usage, string status, string code)
    {
        var known = new AiModelUsage(3, 2, 5);
        using var fixture = new Fixture([Response(known, tool: true), Response(usage, tool: true)]);
        var result = await fixture.RunAsync();

        Assert.Equal(status, result.Status);
        Assert.Equal(2, fixture.Model.Calls);
        Assert.Equal(1, fixture.Estimator.Calls);
        Assert.Equal(1, fixture.Tool.Calls);
        Assert.Equal(5, result.TotalTokens);
        Assert.Equal(0.01m, result.EstimatedCost);
        Assert.Equal(known, result.Usage);
        AssertSkipped(fixture.Audit.Entries[1], code);
    }

    [Theory]
    [InlineData(null, null, 7, 7, false)]
    [InlineData(3, null, 7, 7, false)]
    [InlineData(null, 2, 7, 7, false)]
    [InlineData(3, 4, null, 7, true)]
    [InlineData(3, 4, 7, 7, true)]
    [InlineData(0, 0, null, 0, true)]
    [InlineData(null, null, 0, 0, false)]
    [InlineData(0, 0, 0, 0, true)]
    public async Task ValidUsageAccountsWithoutRewritingProviderValues(
        int? input, int? output, int? total, int expectedTotal, bool exact)
    {
        var usage = new AiModelUsage(input, output, total);
        var response = Response(usage);
        using var fixture = new Fixture([response]);
        var result = await fixture.RunAsync();

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(expectedTotal, result.TotalTokens);
        Assert.Same(usage, result.Usage);
        Assert.Same(usage, response.Usage);
        Assert.Equal(exact ? 1 : 0, fixture.Estimator.Calls);
        Assert.Equal(exact ? 0.01m : expectedTotal / 1000m, result.EstimatedCost);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAggregateComponentsStayUnknown(bool partialFirst)
    {
        var complete = new AiModelUsage(3, 2, 5);
        var partial = new AiModelUsage(null, 1, 4);
        using var fixture = new Fixture([
            Response(partialFirst ? partial : complete, tool: true),
            Response(partialFirst ? complete : partial)]);
        var result = await fixture.RunAsync();

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(new AiModelUsage(null, 3, 9), result.Usage);
        Assert.Equal(9, result.TotalTokens);
        Assert.Equal(1, fixture.Estimator.Calls);
        Assert.Equal(0.014m, result.EstimatedCost);
    }

    [Fact]
    public async Task DerivedTotalsDoNotFabricateAggregateProviderTotals()
    {
        using var fixture = new Fixture([
            Response(new(3, 2, null), tool: true), Response(new(1, 1, 2))]);
        var result = await fixture.RunAsync();
        Assert.Equal(7, result.TotalTokens);
        Assert.Equal(new AiModelUsage(4, 3, null), result.Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CumulativeOverflowStopsBeforeCurrentEstimation(bool componentOverflow)
    {
        var first = componentOverflow ? new AiModelUsage(int.MaxValue, null, 1) : new(int.MaxValue - 1, 0, int.MaxValue - 1);
        var next = componentOverflow ? new AiModelUsage(1, null, 1) : new(2, 0, 2);
        using var fixture = new Fixture([Response(first, tool: true), Response(next, tool: true)]);
        var result = await fixture.RunAsync();

        Assert.Equal("InvalidUsage", result.Status);
        Assert.Equal(first.TotalTokens, result.TotalTokens);
        Assert.Equal(first, result.Usage);
        Assert.Equal(componentOverflow ? 0.001m : 0.01m, result.EstimatedCost);
        Assert.Equal(componentOverflow ? 0 : 1, fixture.Estimator.Calls);
        Assert.Equal(1, fixture.Tool.Calls);
        Assert.Equal(2, fixture.Model.Calls);
        AssertSkipped(fixture.Audit.Entries[1], "invalid_usage");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task EqualityAndOverBudgetStopProposals(bool tokens, bool over)
    {
        var options = new AgenticChatOptions
        {
            MaxTotalTokens = tokens ? 5 : 100,
            MaxEstimatedCost = tokens ? 1m : 0.01m
        };
        using var fixture = new Fixture([Response(new(5, over ? 1 : 0, over ? 6 : 5), tool: true)], options);
        fixture.Estimator.Estimate = over ? 0.02m : 0.01m;
        var result = await fixture.RunAsync();

        Assert.Equal("BudgetExceeded", result.Status);
        Assert.Equal(0, fixture.Tool.Calls);
        Assert.Equal(1, fixture.Model.Calls);
        AssertSkipped(Assert.Single(fixture.Audit.Entries), "budget_exceeded");
    }

    [Fact]
    public async Task CumulativeCostOverflowSaturatesAndStopsAtCostBudget()
    {
        using var fixture = new Fixture([
            Response(new(1, 1, 2), tool: true), Response(new(1, 1, 2), tool: true)],
            new AgenticChatOptions { MaxEstimatedCost = decimal.MaxValue });
        fixture.Estimator.Estimate = decimal.MaxValue / 2m + 1m;

        var result = await fixture.RunAsync();

        Assert.Equal("BudgetExceeded", result.Status);
        Assert.Equal(decimal.MaxValue, result.EstimatedCost);
        Assert.Equal(4, result.TotalTokens);
        Assert.Equal(1, fixture.Tool.Calls);
        Assert.Equal(2, fixture.Model.Calls);
        AssertSkipped(fixture.Audit.Entries[1], "budget_exceeded");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsageStopReasonSurvivesMalformedToolArguments(bool missing)
    {
        var response = Response(missing ? null : new(-1, 0, -1), tool: true);
        response = response with { ProposedToolCalls = [new("call", "Probe", "v1", JsonSerializer.SerializeToElement(42))] };
        using var fixture = new Fixture([response]);
        var result = await fixture.RunAsync();
        var entry = Assert.Single(fixture.Audit.Entries);
        AssertSkipped(entry, missing ? "usage_unavailable" : "invalid_usage");
        Assert.Equal("Invalid", entry.ValidationStatus);
        Assert.Equal(0, fixture.Tool.Calls);
        Assert.Equal(missing ? "UsageUnavailable" : "InvalidUsage", result.Status);
    }

    private static AiModelResponse Response(AiModelUsage? usage, bool tool = false) => new(
        "reply", "mock", "mock", usage, "usage-test",
        tool ? [new("call", "Probe", "v1", JsonSerializer.SerializeToElement(new { }))] : []);

    private static void AssertSkipped(ToolAuditLogEntry entry, string code)
    {
        Assert.Equal("NotExecuted", entry.ExecutionStatus);
        Assert.Equal(code, entry.ErrorCode);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AgenticChatSession session;
        public SequenceModel Model { get; }
        public Estimator Estimator { get; } = new();
        public ProbeTool Tool { get; } = new();
        public AuditRepository Audit { get; } = new();

        public Fixture(AiModelResponse[] responses, AgenticChatOptions? options = null)
        {
            Model = new SequenceModel(responses);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAgenticApplication(new ConfigurationManager());
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IAiModelClient>(Model);
            services.AddSingleton<IAgenticCostEstimator>(Estimator);
            services.AddSingleton<IToolAuditLogRepository>(Audit);
            services.AddSingleton<IAiModelRequestLogger>(new PassthroughLogger());
            provider = services.BuildServiceProvider();
            session = new(Guid.NewGuid(), "tenant", "user", new("usage-test", "mock", 0, 10),
                options ?? new AgenticChatOptions { MaxTotalTokens = int.MaxValue, MaxEstimatedCost = 1m, EstimatedCostPerThousandTokens = 1m },
                [Tool], new([], new PromptMetadata("agentic", "v1", "hash")), false);
        }

        public Task<AgenticChatResponse> RunAsync() => provider.GetRequiredService<AgenticChatLoopRunner>()
            .RunAsync(session, TestContext.Current.CancellationToken);
        public void Dispose() => provider.Dispose();
    }

    private sealed class SequenceModel(AiModelResponse[] responses) : IAiModelClient
    {
        public int Calls { get; private set; }
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
            => Task.FromResult(responses[Calls++]);
    }

    private sealed class Estimator : IAgenticCostEstimator
    {
        public int Calls { get; private set; }
        public decimal? Estimate { get; set; } = 0.01m;
        public Task<decimal?> EstimateAsync(AiModelResponse response, DateTimeOffset usedAtUtc, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.NotNull(response.Usage?.InputTokens);
            Assert.NotNull(response.Usage?.OutputTokens);
            return Task.FromResult(Estimate);
        }
    }

    private sealed class ProbeTool : IAgentTool
    {
        public int Calls { get; private set; }
        public AiToolDefinition Definition { get; } = new("Probe", "Probe", "v1",
            JsonSerializer.SerializeToElement(new { type = "object" }));
        public ToolPolicyMetadata Policy => ToolPolicyMetadata.Allowed("read-only probe");
        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(arguments);
        public Task<ToolExecutionResult> ExecuteAsync(JsonElement sanitizedArguments, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ToolExecutionResult(ToolExecutionStatus.Succeeded, sanitizedArguments));
        }
    }

    private sealed class AuditRepository : IToolAuditLogRepository
    {
        public List<ToolAuditLogEntry> Entries { get; } = [];
        public Task AddAsync(ToolAuditLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class PassthroughLogger : IAiModelRequestLogger
    {
        public Task<AiModelResponse> CompleteAndLogAsync(IAiModelClient modelClient, AiModelRequest request,
            TimeSpan? retrievalLatency, int? embeddingTokens, string? embeddingProvider, string? embeddingModel,
            IReadOnlyList<RetrievedDocumentReference> retrievedDocuments, CancellationToken cancellationToken)
            => modelClient.CompleteAsync(request, cancellationToken);
        public Task LogSucceededWithoutModelAsync(string correlationId, string model, TimeSpan latency,
            int? embeddingTokens, string? embeddingProvider, string? embeddingModel, TimeSpan? retrievalLatency,
            IReadOnlyList<RetrievedDocumentReference> retrievedDocuments) => throw new NotSupportedException();
    }
}
