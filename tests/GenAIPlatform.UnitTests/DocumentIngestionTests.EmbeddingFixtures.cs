using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.UnitTests;

public sealed partial class DocumentIngestionTests
{
    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        public int Calls { get; private set; }

        public List<EmbeddingRequest> Requests { get; } = [];

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(request);
            return Task.FromResult(new EmbeddingResponse(
                [0.1f, 0.2f, 0.3f],
                request.Model,
                "fake",
                InputTokens: 3,
                request.CorrelationId));
        }
    }

    private sealed class FixedEmbeddingClient(
        IReadOnlyList<float>? vector,
        string? model = null,
        string? provider = "fake",
        int? inputTokens = 3)
        : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new EmbeddingResponse(
                vector!,
                model ?? request.Model,
                provider!,
                inputTokens,
                request.CorrelationId));
        }
    }

    private sealed class SlowEmbeddingClient(TimeSpan delay) : IEmbeddingClient
    {
        public async Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new EmbeddingResponse(
                [0.1f, 0.2f, 0.3f],
                request.Model,
                "slow-fake",
                InputTokens: 3,
                request.CorrelationId);
        }
    }

    private sealed class CancellableSlowEmbeddingClient : IEmbeddingClient
    {
        public bool CancellationObserved { get; private set; }

        public async Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }

            return new EmbeddingResponse(
                [0.1f, 0.2f, 0.3f],
                request.Model,
                "cancellable-slow-fake",
                InputTokens: 3,
                request.CorrelationId);
        }
    }

    private sealed class CancellationIgnoringEmbeddingClient(
        TimeSpan delay,
        string provider,
        int inputTokens)
        : IEmbeddingClient
    {
        public async Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, CancellationToken.None);
            return new EmbeddingResponse(
                [0.1f, 0.2f, 0.3f],
                request.Model,
                provider,
                inputTokens,
                request.CorrelationId);
        }
    }

    private sealed class ProviderCancelingEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            throw new TaskCanceledException("Provider-side cancellation.");
        }
    }

    private sealed class CancelingThenThrowingEmbeddingClient(CancellationTokenSource cancellation)
        : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new EmbeddingClientException("fake", "Embedding failed.", "fake_failure");
        }
    }

    private sealed class ThrowingEmbeddingClient(string message = "Embedding failed.") : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            throw new EmbeddingClientException("fake", message, "fake_failure");
        }
    }

    private sealed class CapturingAiRequestLogRepository : IAiRequestLogRepository
    {
        public List<AiRequestLogEntry> Entries { get; } = [];

        public Task AddAsync(
            AiRequestLogEntry entry,
            CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAiRequestLogRepository : IAiRequestLogRepository
    {
        public Task AddAsync(
            AiRequestLogEntry entry,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("telemetry store is unavailable");
        }
    }

    private sealed class EmptyPricingRepository : IPricingRepository
    {
        public Task<PricingRecord?> GetEffectivePricingAsync(
            string provider,
            string model,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<PricingRecord?>(null);
    }

    private sealed class FakeUserContext(
        string? userId,
        string? tenantId,
        bool isAuthenticated = true)
        : IUserContext
    {
        public bool IsAuthenticated => isAuthenticated;

        public string? UserId => userId;

        public string? TenantId => tenantId;

        public IReadOnlyCollection<string> Roles { get; } = ["developer"];

        public IReadOnlyCollection<string> Groups { get; } = ["demo"];
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public CapturingLogger()
            : this([])
        {
        }

        public CapturingLogger(List<LogEntry> entries)
        {
            Entries = entries;
        }

        public List<LogEntry> Entries { get; }

        public IDisposable BeginScope<TState>(TState state)
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
}
