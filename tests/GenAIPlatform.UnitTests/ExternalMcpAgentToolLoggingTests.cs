using System.Text.Json;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Infrastructure.Mcp;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.UnitTests;

public sealed class ExternalMcpAgentToolLoggingTests
{
    private const string Secret = "PRIVATE_PAYLOAD_PASSWORD_SQL_VECTOR";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwallowedFailureLogsOnlyBoundedIdentityAndExceptionType(bool cancellation)
    {
        Exception failure = cancellation ? new OperationCanceledException(Secret) : new InvalidOperationException(Secret);
        var manager = new StubManager((_, _) => Task.FromException<ExternalMcpToolCallResult>(failure));
        var logs = new CapturingLogger();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var source = new ExternalMcpAgentToolSource(manager, factory);
        var tool = Assert.Single(source.GetAvailableTools());

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { password = Secret }),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal("mcp_server_unavailable", result.ErrorCode);
        Assert.Equal(1, manager.Calls);
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(4001, entry.Event.Id);
        Assert.Equal("ExternalMcpToolExecutionFailed", entry.Event.Name);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(["ExceptionType", "ServerName", "ToolName", "{OriginalFormat}"], entry.Fields.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(failure.GetType().Name, entry.Fields["ExceptionType"]);
        foreach (var key in new[] { "ServerName", "ToolName" })
        {
            var identity = Assert.IsType<string>(entry.Fields[key]);
            Assert.InRange(identity.Length, 1, 64);
            Assert.Matches("^[a-z0-9_]+$", identity);
        }

        Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(entry.Fields), StringComparison.Ordinal);
        Assert.True(manager.Snapshot.ServerName.Length > 64);
        Assert.True(manager.Snapshot.OriginalName.Length > 64);
    }

    [Fact]
    public async Task CallerCancellationPropagatesOriginalWithoutWarning()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var failure = new OperationCanceledException(Secret, canceled.Token);
        var manager = new StubManager((_, _) => Task.FromException<ExternalMcpToolCallResult>(failure));
        var logs = new CapturingLogger();
        var tool = new ExternalMcpAgentTool(manager, manager.Snapshot, logs);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), canceled.Token));

        Assert.Same(failure, actual);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task PostDispatchCallerCancellationKeepsUnknownOutcomeForAuditWithoutWarning()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var manager = new StubManager((_, _) => Task.FromException<ExternalMcpToolCallResult>(
            new ExternalMcpCallCanceledException(canceled.Token)));
        var logs = new CapturingLogger();
        var tool = new ExternalMcpAgentTool(manager, manager.Snapshot, logs);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), canceled.Token);

        Assert.Equal("mcp_tool_outcome_unknown", result.ErrorCode);
        Assert.Null(result.PayloadMetadata);
        Assert.Equal(1, manager.Calls);
        Assert.Empty(logs.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedResultDoesNotLogPayload(bool isError)
    {
        var manager = new StubManager((_, _) => Task.FromResult(new ExternalMcpToolCallResult(
            isError, JsonSerializer.SerializeToElement(new { secret = Secret }), Secret)));
        var logs = new CapturingLogger();
        var tool = new ExternalMcpAgentTool(manager, manager.Snapshot, logs);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { secret = Secret }),
            TestContext.Current.CancellationToken);

        Assert.Equal(isError ? ToolExecutionStatus.Failed : ToolExecutionStatus.Succeeded, result.Status);
        Assert.Empty(logs.Entries);
    }

    private sealed class StubManager(
        Func<IReadOnlyDictionary<string, object?>?, CancellationToken, Task<ExternalMcpToolCallResult>> call)
        : IExternalMcpConnectionManager
    {
        public ExternalMcpToolSnapshot Snapshot { get; } = new(
            "Server\r\n/" + new string('S', 150), "Tool\t/" + new string('T', 150), "mcp_server_tool",
            Secret, Secret, JsonSerializer.SerializeToElement(new { type = "object", description = Secret }),
            false, TimeSpan.FromSeconds(1));

        public int Calls { get; private set; }

        public IReadOnlyList<ExternalMcpServerSnapshot> GetSnapshots() =>
            [new(Snapshot.ServerName, 0, ExternalMcpServerStatus.Available, [Snapshot])];

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ExternalMcpToolCallResult> CallToolAsync(ExternalMcpToolSnapshot tool,
            IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return call(arguments, cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<ExternalMcpAgentTool>, ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(new(logLevel, eventId, exception,
                formatter(state, exception), ((IEnumerable<KeyValuePair<string, object?>>)state!)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value)));
    }

    private sealed record LogEntry(LogLevel Level, EventId Event, Exception? Exception, string Message,
        IReadOnlyDictionary<string, object?> Fields);
}
