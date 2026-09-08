using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Infrastructure.Configuration;
using GenAIPlatform.Infrastructure.Postgres;
using GenAIPlatform.Infrastructure.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GenAIPlatform.UnitTests;

public sealed class PostgresRagSearchExecutorTests
{
    private const string Secret = "PRIVATE_SQL_QUERY_VECTOR_PASSWORD";

    [Theory]
    [InlineData("42P01", "retrieval_schema_error")]
    [InlineData("42703", "retrieval_schema_error")]
    [InlineData("42883", "retrieval_schema_error")]
    [InlineData("42704", "retrieval_schema_error")]
    [InlineData("42501", "retrieval_query_failed")]
    [InlineData("XX000", "retrieval_query_failed")]
    [InlineData("npgsql", "retrieval_unavailable")]
    [InlineData("timeout", "retrieval_unavailable")]
    public async Task InfrastructureFailureMapsAndLogsOnlyCodeAndExceptionType(string kind, string errorCode)
    {
        Exception failure = kind switch
        {
            "npgsql" => new NpgsqlException(Secret),
            "timeout" => new TimeoutException(Secret),
            _ => new PostgresException(Secret, "ERROR", "ERROR", kind, detail: Secret,
                hint: Secret, internalQuery: Secret, schemaName: Secret, tableName: Secret)
        };
        using var provider = CreateProvider(failure);
        var logger = new CapturingLogger();
        var executor = new PostgresRagSearchExecutor(new PostgresRagConnectionFactory(provider),
            new RagVectorSearchErrorMapper(), logger);

        var actual = await Assert.ThrowsAsync<RagVectorSearchException>(() => executor.SearchAsync(Query(),
            TestContext.Current.CancellationToken));

        Assert.Equal(errorCode, actual.ErrorCode);
        Assert.Same(failure, actual.InnerException);
        Assert.DoesNotContain(Secret, actual.Message, StringComparison.Ordinal);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(5001, entry.Event.Id);
        Assert.Equal("RagSearchInfrastructureFailure", entry.Event.Name);
        Assert.Null(entry.Exception);
        Assert.Equal(["ErrorCode", "ExceptionType", "{OriginalFormat}"], entry.Fields.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(errorCode, entry.Fields["ErrorCode"]);
        Assert.InRange(Assert.IsType<string>(entry.Fields["ErrorCode"]).Length, 1, 64);
        Assert.Equal(failure.GetType().Name, entry.Fields["ExceptionType"]);
        Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join(" ", entry.Fields.Values), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("invalid_operation")]
    [InlineData("contract")]
    [InlineData("cancellation")]
    [InlineData("caller_cancellation")]
    public async Task NonInfrastructureExceptionPropagatesOriginalWithoutMappingOrWarning(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        if (kind == "caller_cancellation")
        {
            await cancellation.CancelAsync();
        }

        Exception failure = kind switch
        {
            "argument" => new ArgumentException(Secret),
            "invalid_operation" => new InvalidOperationException(Secret),
            "contract" => new RagVectorSearchException("postgres", Secret, Secret),
            _ => new OperationCanceledException(Secret, cancellation.Token)
        };
        using var provider = CreateProvider(failure);
        var logger = new CapturingLogger();
        var executor = new PostgresRagSearchExecutor(new PostgresRagConnectionFactory(provider),
            new RagVectorSearchErrorMapper(), logger);

        var actual = await Record.ExceptionAsync(() => executor.SearchAsync(Query(), cancellation.Token));

        Assert.Same(failure, actual);
        Assert.NotNull(actual);
        Assert.Contains(nameof(FailingOptions), actual.StackTrace, StringComparison.Ordinal);
        Assert.Contains(nameof(PostgresRagSearchExecutor.SearchAsync), actual.StackTrace, StringComparison.Ordinal);
        Assert.Empty(logger.Entries);
    }

    private static PostgresDataSourceProvider CreateProvider(Exception failure) => new(
        new ConfigurationBuilder().Build(), new FailingOptions(failure));

    private static RagVectorSearchQuery Query() => new([0.123456f, 0.987654f], Secret, Secret,
        Secret, Secret, 3, 0.2, []);

    private sealed class FailingOptions(Exception failure) : IOptions<PostgresOptions>
    {
        public PostgresOptions Value => throw failure;
    }

    private sealed class CapturingLogger : ILogger<PostgresRagSearchExecutor>
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
