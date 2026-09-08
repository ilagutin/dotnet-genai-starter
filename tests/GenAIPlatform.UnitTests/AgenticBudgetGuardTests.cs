using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Agentic.Chat;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Domain.Prompts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.UnitTests;

public sealed class AgenticBudgetGuardTests
{
    private const string Secret = "PRIVATE_RESPONSE_PRICING_PASSWORD";
    private static readonly AgenticChatOptions Options = new() { EstimatedCostPerThousandTokens = 0.00345678m };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallbackKeepsArithmeticAndLogsOncePerConversationInOneScope(bool throws)
    {
        var logger = new CapturingLogger();
        var estimator = new StubEstimator(() => throws
            ? Task.FromException<decimal?>(new InvalidOperationException(Secret))
            : Task.FromResult<decimal?>(null));
        var services = new ServiceCollection();
        services.AddSingleton<IAgenticCostEstimator>(estimator);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILogger<AgenticBudgetGuard>>(logger);
        services.AddScoped<AgenticBudgetGuard>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var guard = scope.ServiceProvider.GetRequiredService<AgenticBudgetGuard>();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = CreateLoopState(firstId, "first-correlation");
        var second = CreateLoopState(secondId, "second-correlation");

        foreach (var state in new[] { first, second })
        {
            await state.ApplyModelResponseAsync(Response(123), guard, TestContext.Current.CancellationToken);
            await state.ApplyModelResponseAsync(Response(123), guard, TestContext.Current.CancellationToken);
            Assert.Equal(246, state.TotalTokens);
            Assert.Equal(0.00085036m, state.EstimatedCost);
        }

        Assert.Equal(4, estimator.Calls);
        Assert.Equal(2, logger.Entries.Count);
        var firstWarning = Assert.Single(logger.Entries,
            entry => Equals(firstId, entry.Fields["ConversationId"]));
        var secondWarning = Assert.Single(logger.Entries,
            entry => Equals(secondId, entry.Fields["ConversationId"]));
        Assert.Equal("first-correlation", firstWarning.Fields["CorrelationId"]);
        Assert.Equal("second-correlation", secondWarning.Fields["CorrelationId"]);
        Assert.All(logger.Entries, entry => AssertWarning(entry,
            throws ? "estimator_failed" : "pricing_unavailable", throws ? nameof(InvalidOperationException) : null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task MissingOrZeroUsageRetainsExistingZeroFallback(int? tokens)
    {
        var logger = new CapturingLogger();
        var guard = new AgenticBudgetGuard(new StubEstimator(() => Task.FromResult<decimal?>(null)),
            TimeProvider.System, logger);

        var cost = await guard.EstimateResponseCostAsync(Response(tokens), Options,
            new AgenticBudgetFallbackState(Guid.NewGuid(), "test-correlation"), TestContext.Current.CancellationToken);

        Assert.Equal(0m, cost);
        AssertWarning(Assert.Single(logger.Entries), "pricing_unavailable", null);
    }

    [Fact]
    public async Task SuccessfulEstimateDoesNotWarnOrConsumeFirstFallbackWarning()
    {
        var logger = new CapturingLogger();
        var estimator = new StubEstimator(() => Task.FromResult<decimal?>(0.01234567m));
        var guard = new AgenticBudgetGuard(estimator, TimeProvider.System, logger);
        var state = new AgenticBudgetFallbackState(Guid.NewGuid(), "test-correlation");

        var cost = await guard.EstimateResponseCostAsync(Response(123), Options, state,
            TestContext.Current.CancellationToken);

        Assert.Equal(0.01234567m, cost);
        Assert.Empty(logger.Entries);
        estimator.Estimate = () => Task.FromResult<decimal?>(null);
        await guard.EstimateResponseCostAsync(Response(123), Options, state, TestContext.Current.CancellationToken);
        estimator.Estimate = () => Task.FromException<decimal?>(new InvalidOperationException(Secret));
        await guard.EstimateResponseCostAsync(Response(123), Options, state, TestContext.Current.CancellationToken);
        AssertWarning(Assert.Single(logger.Entries), "pricing_unavailable", null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPropagatesWithoutFallbackWarning(bool callerCanceled)
    {
        using var source = new CancellationTokenSource();
        if (callerCanceled)
        {
            await source.CancelAsync();
        }

        var failure = new OperationCanceledException(Secret, source.Token);
        var logger = new CapturingLogger();
        var guard = new AgenticBudgetGuard(new StubEstimator(() => Task.FromException<decimal?>(failure)),
            TimeProvider.System, logger);
        var state = new AgenticBudgetFallbackState(Guid.NewGuid(), "test-correlation");

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => guard.EstimateResponseCostAsync(
            Response(123), Options, state, source.Token));

        Assert.Same(failure, actual);
        Assert.Empty(logger.Entries);
        Assert.True(state.TryMark());
    }

    [Fact]
    public void BudgetEqualityRetainsExistingStrictGreaterThanComparison()
    {
        var logger = new CapturingLogger();
        var guard = new AgenticBudgetGuard(new StubEstimator(() => Task.FromResult<decimal?>(null)),
            TimeProvider.System, logger);
        Assert.False(guard.IsExceeded(Options.MaxTotalTokens, Options.MaxEstimatedCost, Options));
        Assert.True(guard.IsExceeded(Options.MaxTotalTokens + 1, 0m, Options));
        Assert.True(guard.IsExceeded(0, Options.MaxEstimatedCost + 0.00000001m, Options));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ConcurrentFallbackMarkHasExactlyOneWinner()
    {
        var state = new AgenticBudgetFallbackState(Guid.NewGuid(), "test-correlation");
        var marks = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(state.TryMark,
            TestContext.Current.CancellationToken)));
        Assert.Single(marks, static marked => marked);
    }

    private static AiModelResponse Response(int? tokens) => new(Secret, Secret, Secret,
        tokens is null ? null : new AiModelUsage(100, 23, tokens), Secret);

    private static AgenticChatLoopState CreateLoopState(Guid conversationId, string correlationId) => new(new AgenticChatSession(
        conversationId, "tenant", "user", new ModelGatewayRequestSettings(correlationId, "mock", 0, 100),
        Options, [], new AgenticPromptMessages([], new PromptMetadata("agentic", "v1", "hash")), false));

    private static void AssertWarning(LogEntry entry, string reason, string? exceptionType)
    {
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(4002, entry.Event.Id);
        Assert.Equal("AgenticBudgetFallbackUsed", entry.Event.Name);
        Assert.Null(entry.Exception);
        Assert.Equal(["ConversationId", "CorrelationId", "ExceptionType", "Reason", "{OriginalFormat}"],
            entry.Fields.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.NotEqual(Guid.Empty, Assert.IsType<Guid>(entry.Fields["ConversationId"]));
        Assert.NotEmpty(Assert.IsType<string>(entry.Fields["CorrelationId"]));
        Assert.Equal(reason, entry.Fields["Reason"]);
        Assert.Equal(exceptionType, entry.Fields["ExceptionType"]);
        Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join(" ", entry.Fields.Values), StringComparison.Ordinal);
    }

    private sealed class StubEstimator(Func<Task<decimal?>> estimate) : IAgenticCostEstimator
    {
        public Func<Task<decimal?>> Estimate { get; set; } = estimate;
        public int Calls { get; private set; }
        public Task<decimal?> EstimateAsync(AiModelResponse response, DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Estimate();
        }
    }

    private sealed class CapturingLogger : ILogger<AgenticBudgetGuard>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(new(logLevel, eventId, exception,
                formatter(state, exception), ((IEnumerable<KeyValuePair<string, object?>>)state!)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value)));
    }

    private sealed record LogEntry(LogLevel Level, EventId Event, Exception? Exception, string Message,
        IReadOnlyDictionary<string, object?> Fields);
}
