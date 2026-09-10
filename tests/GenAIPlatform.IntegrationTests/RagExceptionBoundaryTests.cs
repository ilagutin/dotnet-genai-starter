using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Infrastructure.Configuration;
using GenAIPlatform.Infrastructure.Postgres;
using GenAIPlatform.Infrastructure.Retrieval;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.IntegrationTests;

public sealed class RagExceptionBoundaryTests
{
    [Fact]
    public async Task ProgrammingFailureInRealSearchExecutorReturns500AndKeepsOriginalDispatchStack()
    {
        var failure = new InvalidOperationException("Injected retrieval programming failure.");
        var logs = new CapturingLoggerProvider();
        var model = new CountingModelClient();
        var embeddings = new CountingEmbeddingClient();
        var options = new FailingOptions(failure);
        using var dataSource = new PostgresDataSourceProvider(new ConfigurationBuilder().Build(), options);
        ReadySearchStore? store = null;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(logs));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserContext>();
                services.AddScoped<IUserContext, TestUserContext>();
                services.RemoveAll<IAiModelClient>();
                services.AddSingleton<IAiModelClient>(model);
                services.RemoveAll<IEmbeddingClient>();
                services.AddSingleton<IEmbeddingClient>(embeddings);
                services.RemoveAll<IRagVectorSearchStore>();
                services.AddScoped(_ => new PostgresRagConnectionFactory(dataSource));
                services.AddScoped<PostgresRagVectorSearchStore>();
                services.AddScoped<IRagVectorSearchStore>(provider => store = new ReadySearchStore(
                    provider.GetRequiredService<PostgresRagVectorSearchStore>()));
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var response = await client.PostAsJsonAsync("/api/v1/chat/rag",
            new { message = "Find reference material.", correlationId = "rag-programming-failure" },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("retrieval_unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("retrieval_query_failed", body, StringComparison.Ordinal);
        Assert.DoesNotContain(failure.Message, body, StringComparison.Ordinal);
        Assert.NotNull(store);
        Assert.Equal(1, store.ReadinessCalls);
        Assert.Equal(1, store.SearchCalls);
        Assert.Equal(1, options.Reads);
        Assert.Equal(1, embeddings.Calls);
        Assert.Equal(0, model.Calls);
        var dispatch = Assert.Single(logs.Entries, static entry => entry.Event.Id == 3002);
        Assert.Equal(LogLevel.Warning, dispatch.Level);
        Assert.Contains("DispatchLoggingBehavior", dispatch.Category, StringComparison.Ordinal);
        Assert.Same(failure, dispatch.Exception);
        Assert.NotNull(dispatch.Exception);
        Assert.Contains(nameof(FailingOptions), dispatch.Exception.StackTrace, StringComparison.Ordinal);
        Assert.Contains(nameof(PostgresRagSearchExecutor), dispatch.Exception.StackTrace, StringComparison.Ordinal);
        Assert.Contains(nameof(PostgresRagVectorSearchStore), dispatch.Exception.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, static entry => entry.Event.Id == 5001);
    }

    // Only readiness is bypassed. Search exercises the production store and executor;
    // the failing options getter runs before any connection or provider can be opened.
    private sealed class ReadySearchStore(PostgresRagVectorSearchStore inner) : IRagVectorSearchStore
    {
        public int ReadinessCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public Task CheckReadinessAsync(CancellationToken cancellationToken)
        {
            ReadinessCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(RagVectorSearchQuery query,
            CancellationToken cancellationToken)
        {
            SearchCalls++;
            return inner.SearchAsync(query, cancellationToken);
        }
    }

    private sealed class FailingOptions(Exception failure) : IOptions<PostgresOptions>
    {
        public int Reads { get; private set; }
        public PostgresOptions Value
        {
            get
            {
                Reads++;
                throw failure;
            }
        }
    }

    private sealed class TestUserContext : IUserContext
    {
        public bool IsAuthenticated => true;
        public string UserId => "test-user";
        public string TenantId => "test-tenant";
        public IReadOnlyCollection<string> Roles => [];
        public IReadOnlyCollection<string> Groups => [];
    }

    private sealed class CountingModelClient : IAiModelClient
    {
        public int Calls { get; private set; }
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Model must not run after retrieval failure.");
        }
    }

    private sealed class CountingEmbeddingClient : IEmbeddingClient
    {
        public int Calls { get; private set; }
        public Task<EmbeddingResponse> CreateEmbeddingAsync(EmbeddingRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new EmbeddingResponse([1f, 0.5f], "test-embedding", "test", 4, request.CorrelationId));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Enqueue(new(category, logLevel, eventId, exception));
    }

    private sealed record LogEntry(string Category, LogLevel Level, EventId Event, Exception? Exception);
}
