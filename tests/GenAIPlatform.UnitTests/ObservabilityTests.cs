using System.Net;
using GenAIPlatform.Application.Core.Configuration;
using GenAIPlatform.Application.Core.Exceptions;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Usage.GetUsage;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Domain.Prompts;
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Observability.Logging;
using GenAIPlatform.Infrastructure.Observability.Pricing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed class ObservabilityTests
{
    [Fact]
    public async Task CostEstimator_UsesPricingEffectiveAtRequestTime()
    {
        var pricingRepository = new InMemoryPricingRepository([
            new PricingRecord(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "fake",
                "test-model",
                "USD",
                InputTokenPricePerMillion: 1.00m,
                OutputTokenPricePerMillion: 2.00m,
                EmbeddingTokenPricePerMillion: null,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-05-01T00:00:00Z")),
            new PricingRecord(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                "fake",
                "test-model",
                "USD",
                InputTokenPricePerMillion: 10.00m,
                OutputTokenPricePerMillion: 20.00m,
                EmbeddingTokenPricePerMillion: null,
                DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
                EffectiveToUtc: null)
        ]);
        var estimator = new AiCostEstimator(pricingRepository);

        var beforeChange = await estimator.EstimateAsync(
            "fake",
            "test-model",
            new AiModelUsage(1_000, 2_000, 3_000),
            embeddingTokens: null,
            embeddingProvider: null,
            embeddingModel: null,
            DateTimeOffset.Parse("2026-04-15T00:00:00Z"),
            CancellationToken.None);
        var afterChange = await estimator.EstimateAsync(
            "fake",
            "test-model",
            new AiModelUsage(1_000, 2_000, 3_000),
            embeddingTokens: null,
            embeddingProvider: null,
            embeddingModel: null,
            DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
            CancellationToken.None);

        Assert.NotNull(beforeChange);
        Assert.Equal(0.00500000m, beforeChange.Amount);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), beforeChange.PricingRecordId);
        Assert.NotNull(afterChange);
        Assert.Equal(0.05000000m, afterChange.Amount);
        Assert.Equal(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), afterChange.PricingRecordId);
    }

    [Fact]
    public async Task CostEstimator_PricesEmbeddingTokensWithEmbeddingModelPricing()
    {
        var pricingRepository = new InMemoryPricingRepository([
            new PricingRecord(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "chat-provider",
                "chat-model",
                "USD",
                InputTokenPricePerMillion: 1.00m,
                OutputTokenPricePerMillion: 2.00m,
                EmbeddingTokenPricePerMillion: null,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                EffectiveToUtc: null),
            new PricingRecord(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                "embedding-provider",
                "embedding-model",
                "USD",
                InputTokenPricePerMillion: 0m,
                OutputTokenPricePerMillion: 0m,
                EmbeddingTokenPricePerMillion: 100.00m,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                EffectiveToUtc: null)
        ]);
        var estimator = new AiCostEstimator(pricingRepository);

        var cost = await estimator.EstimateAsync(
            "chat-provider",
            "chat-model",
            new AiModelUsage(1_000, 2_000, 3_000),
            embeddingTokens: 3_000,
            embeddingProvider: "embedding-provider",
            embeddingModel: "embedding-model",
            DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
            CancellationToken.None);

        Assert.NotNull(cost);
        Assert.Equal(0.30500000m, cost.Amount);
    }

    [Fact]
    public async Task CostEstimator_PricesEmbeddingOnlyWhenModelUsageIsAbsent()
    {
        var pricingRepository = new InMemoryPricingRepository([
            new PricingRecord(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                "embedding-provider",
                "embedding-model",
                "USD",
                InputTokenPricePerMillion: 0m,
                OutputTokenPricePerMillion: 0m,
                EmbeddingTokenPricePerMillion: 100.00m,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                EffectiveToUtc: null)
        ]);
        var estimator = new AiCostEstimator(pricingRepository);

        var cost = await estimator.EstimateAsync(
            "no-model",
            "resolved-chat-model",
            usage: null,
            embeddingTokens: 3_000,
            embeddingProvider: "embedding-provider",
            embeddingModel: "embedding-model",
            DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
            CancellationToken.None);

        Assert.NotNull(cost);
        Assert.Equal(0.30000000m, cost.Amount);
        Assert.Equal("USD", cost.Currency);
        Assert.Equal(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), cost.PricingRecordId);
    }

    [Fact]
    public async Task CostEstimator_DoesNotBlendModelAndEmbeddingCurrencies()
    {
        var pricingRepository = new InMemoryPricingRepository([
            new PricingRecord(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "chat-provider",
                "chat-model",
                "USD",
                InputTokenPricePerMillion: 1.00m,
                OutputTokenPricePerMillion: 2.00m,
                EmbeddingTokenPricePerMillion: null,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                EffectiveToUtc: null),
            new PricingRecord(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                "embedding-provider",
                "embedding-model",
                "EUR",
                InputTokenPricePerMillion: 0m,
                OutputTokenPricePerMillion: 0m,
                EmbeddingTokenPricePerMillion: 100.00m,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                EffectiveToUtc: null)
        ]);
        var estimator = new AiCostEstimator(pricingRepository);

        var cost = await estimator.EstimateAsync(
            "chat-provider",
            "chat-model",
            new AiModelUsage(1_000, 2_000, 3_000),
            embeddingTokens: 3_000,
            embeddingProvider: "embedding-provider",
            embeddingModel: "embedding-model",
            DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
            CancellationToken.None);

        Assert.Null(cost);
    }

    [Fact]
    public async Task UsageQueryHandler_ScopesNonAdminQueriesToAuthenticatedUserAndTenant()
    {
        var repository = new CapturingUsageRepository();
        var handler = new UsageQueryHandler(
            repository,
            new UsageQueryScopeResolver(new FakeUserContext()));

        await handler.HandleAsync(
            new UsageQuery(
                DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
                UserId: null,
                TenantId: null,
                Model: "test-model"),
            CancellationToken.None);

        Assert.NotNull(repository.Query);
        Assert.Equal("alice", repository.Query.UserId);
        Assert.Equal("tenant-a", repository.Query.TenantId);
        Assert.Equal("test-model", repository.Query.Model);
    }

    [Fact]
    public async Task UsageQueryHandler_RejectsNonAdminCrossTenantOrUserFilters()
    {
        var handler = new UsageQueryHandler(
            new CapturingUsageRepository(),
            new UsageQueryScopeResolver(new FakeUserContext()));

        await Assert.ThrowsAsync<ForbiddenRequestException>(() =>
            handler.HandleAsync(
                new UsageQuery(UserId: "bob", TenantId: "tenant-a"),
                CancellationToken.None));

        await Assert.ThrowsAsync<ForbiddenRequestException>(() =>
            handler.HandleAsync(
                new UsageQuery(UserId: "alice", TenantId: "tenant-b"),
                CancellationToken.None));
    }

    [Fact]
    public async Task LoggingService_StoresSanitizedPromptMetadataAndRetrievalReferences()
    {
        var repository = new CapturingAiRequestLogRepository();
        var service = CreateService(repository);
        var prompt = new PromptMetadata(
            "rag-chat",
            "v1",
            new string('a', 64));
        var request = new AiModelRequest(
            "correlation-1",
            "test-model",
            [new AiChatMessage(AiMessageRole.User, "private prompt content")],
            Prompt: prompt);

        await service.CompleteAndLogAsync(
            new SuccessfulModelClient(),
            request,
            TimeSpan.FromMilliseconds(12),
            embeddingTokens: 7,
            embeddingProvider: "fake",
            embeddingModel: "test-embedding",
            retrievedDocuments: [new RetrievedDocumentReference("1", Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), Guid.NewGuid())],
            CancellationToken.None);

        var entry = Assert.Single(repository.Entries);
        Assert.Equal("v1", entry.ApiVersion);
        Assert.Equal("alice", entry.UserId);
        Assert.Equal("tenant-a", entry.TenantId);
        Assert.Equal("correlation-1", entry.CorrelationId);
        Assert.Equal("Succeeded", entry.Status);
        Assert.Equal(prompt, entry.Prompt);
        Assert.Equal(7, entry.EmbeddingTokens);
        Assert.Equal(TimeSpan.FromMilliseconds(12), entry.RetrievalLatency);
        var documentReference = Assert.Single(entry.RetrievedDocuments);
        Assert.Equal(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), documentReference.DocumentId);
        Assert.DoesNotContain(repository.Entries.SelectMany(static logged => logged.RetrievedDocuments), static reference => reference.ReferenceId == "private prompt content");
    }

    [Fact]
    public async Task LoggingService_UsesConfiguredApplicationApiVersion()
    {
        var repository = new CapturingAiRequestLogRepository();
        var service = CreateService(
            repository,
            applicationOptions: new ApplicationOptions
            {
                ApiVersion = "v-test",
                RunnerVersion = "runner-test"
            });

        await service.CompleteAndLogAsync(
            new SuccessfulModelClient(),
            new AiModelRequest(
                "correlation-version",
                "test-model",
                [new AiChatMessage(AiMessageRole.User, "hello")]),
            retrievalLatency: null,
            embeddingTokens: null,
            embeddingProvider: null,
            embeddingModel: null,
            retrievedDocuments: [],
            CancellationToken.None);

        var entry = Assert.Single(repository.Entries);
        Assert.Equal("v-test", entry.ApiVersion);
    }

    [Fact]
    public async Task LoggingService_StoresNoModelOutcomeWithEmbeddingCostOnly()
    {
        var repository = new CapturingAiRequestLogRepository();
        var service = CreateService(
            repository,
            new InMemoryPricingRepository([
                new PricingRecord(
                    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    "embedding-provider",
                    "embedding-model",
                    "USD",
                    InputTokenPricePerMillion: 0m,
                    OutputTokenPricePerMillion: 0m,
                    EmbeddingTokenPricePerMillion: 100.00m,
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    EffectiveToUtc: null)
            ]));

        await service.LogSucceededWithoutModelAsync(
            "no-context-correlation",
            "resolved-chat-model",
            TimeSpan.FromMilliseconds(15),
            embeddingTokens: 3_000,
            embeddingProvider: "embedding-provider",
            embeddingModel: "embedding-model",
            retrievalLatency: TimeSpan.FromMilliseconds(15),
            retrievedDocuments: []);

        var entry = Assert.Single(repository.Entries);
        Assert.Equal("Succeeded", entry.Status);
        Assert.Equal("no-model", entry.Provider);
        Assert.Equal("resolved-chat-model", entry.Model);
        Assert.Equal("no-context-correlation", entry.CorrelationId);
        Assert.Null(entry.InputTokens);
        Assert.Null(entry.OutputTokens);
        Assert.Null(entry.TotalTokens);
        Assert.Equal(3_000, entry.EmbeddingTokens);
        Assert.Equal(0.30000000m, entry.EstimatedCost);
        Assert.Equal("USD", entry.CostCurrency);
        Assert.Null(entry.Prompt);
        Assert.Equal(TimeSpan.FromMilliseconds(15), entry.RetrievalLatency);
        Assert.Empty(entry.RetrievedDocuments);
    }

    [Fact]
    public async Task LoggingService_StoresDiscardedEmbeddingUsageWithoutPromptContent()
    {
        var repository = new CapturingAiRequestLogRepository();
        var service = CreateService(
            repository,
            new InMemoryPricingRepository([
                new PricingRecord(
                    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    "embedding-provider",
                    "embedding-model",
                    "USD",
                    InputTokenPricePerMillion: 0m,
                    OutputTokenPricePerMillion: 0m,
                    EmbeddingTokenPricePerMillion: 100.00m,
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    EffectiveToUtc: null)
            ]));

        await service.LogDiscardedEmbeddingAsync(
            "indexing-document-1-job-1",
            "embedding-provider",
            "embedding-model",
            embeddingTokens: 3_000,
            TimeSpan.FromMilliseconds(25));

        var entry = Assert.Single(repository.Entries);
        Assert.Equal("Succeeded", entry.Status);
        Assert.Equal("indexing_abandoned", entry.ErrorCode);
        Assert.Equal("embedding-provider", entry.Provider);
        Assert.Equal("embedding-model", entry.Model);
        Assert.Equal("indexing-document-1-job-1", entry.CorrelationId);
        Assert.Null(entry.InputTokens);
        Assert.Null(entry.OutputTokens);
        Assert.Null(entry.TotalTokens);
        Assert.Equal(3_000, entry.EmbeddingTokens);
        Assert.Equal(0.30000000m, entry.EstimatedCost);
        Assert.Equal("USD", entry.CostCurrency);
        Assert.Null(entry.Prompt);
        Assert.Null(entry.RetrievalLatency);
        Assert.Empty(entry.RetrievedDocuments);
    }

    [Fact]
    public async Task LoggingService_RecordsNormalizedModelFailureAndRethrows()
    {
        var repository = new CapturingAiRequestLogRepository();
        var service = CreateService(repository);
        var request = new AiModelRequest(
            "correlation-2",
            "test-model",
            [new AiChatMessage(AiMessageRole.User, "message")]);

        await Assert.ThrowsAsync<AiModelException>(() =>
            service.CompleteAndLogAsync(
                new ThrowingModelClient(),
                request,
                retrievalLatency: null,
                embeddingTokens: null,
                embeddingProvider: null,
                embeddingModel: null,
                retrievedDocuments: [],
                CancellationToken.None));

        var entry = Assert.Single(repository.Entries);
        Assert.Equal("Failed", entry.Status);
        Assert.Equal("fake", entry.Provider);
        Assert.Equal("rate_limited", entry.ErrorCode);
    }

    [Fact]
    public async Task LoggingService_RethrowsModelFailureWhenFailClosedFailureLogPersistenceFails()
    {
        var service = CreateService(
            new ThrowingAiRequestLogRepository(),
            failureMode: AiRequestLoggingFailureMode.FailClosed);
        var request = new AiModelRequest(
            "correlation-5",
            "test-model",
            [new AiChatMessage(AiMessageRole.User, "message")]);

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            service.CompleteAndLogAsync(
                new ThrowingModelClient(),
                request,
                retrievalLatency: null,
                embeddingTokens: null,
                embeddingProvider: null,
                embeddingModel: null,
                retrievedDocuments: [],
                CancellationToken.None));

        Assert.Equal("rate_limited", exception.ErrorCode);
    }

    [Fact]
    public async Task LoggingService_RethrowsCancellationWhenFailClosedFailureLogPersistenceFails()
    {
        var service = CreateService(
            new ThrowingAiRequestLogRepository(),
            failureMode: AiRequestLoggingFailureMode.FailClosed);
        var request = new AiModelRequest(
            "correlation-6",
            "test-model",
            [new AiChatMessage(AiMessageRole.User, "message")]);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.CompleteAndLogAsync(
                new CancelingModelClient(),
                request,
                retrievalLatency: null,
                embeddingTokens: null,
                embeddingProvider: null,
                embeddingModel: null,
                retrievedDocuments: [],
                CancellationToken.None));
    }

    [Fact]
    public async Task LoggingService_FailsOpenWhenTelemetryPersistenceFails()
    {
        var service = CreateService(new ThrowingAiRequestLogRepository());
        var request = new AiModelRequest(
            "correlation-3",
            "test-model",
            [new AiChatMessage(AiMessageRole.User, "message")]);

        var response = await service.CompleteAndLogAsync(
            new SuccessfulModelClient(),
            request,
            retrievalLatency: null,
            embeddingTokens: null,
            embeddingProvider: null,
            embeddingModel: null,
            retrievedDocuments: [],
            CancellationToken.None);

        Assert.Equal("answer", response.Content);
    }

    [Fact]
    public async Task LoggingService_FailsClosedWhenTelemetryPersistenceFailsAndConfigured()
    {
        var service = CreateService(
            new ThrowingAiRequestLogRepository(),
            failureMode: AiRequestLoggingFailureMode.FailClosed);
        var request = new AiModelRequest(
            "correlation-4",
            "test-model",
            [new AiChatMessage(AiMessageRole.User, "message")]);

        var exception = await Assert.ThrowsAsync<AiRequestLoggingException>(() =>
            service.CompleteAndLogAsync(
                new SuccessfulModelClient(),
                request,
                retrievalLatency: null,
                embeddingTokens: null,
                embeddingProvider: null,
                embeddingModel: null,
                retrievedDocuments: [],
                CancellationToken.None));

        Assert.Equal("AI request logging failed.", exception.Message);
        Assert.DoesNotContain("telemetry store is unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AiModelRequestLoggingService CreateService(
        IAiRequestLogRepository repository,
        IPricingRepository? pricingRepository = null,
        AiRequestLoggingFailureMode failureMode = AiRequestLoggingFailureMode.FailOpen,
        ApplicationOptions? applicationOptions = null)
    {
        return new AiModelRequestLoggingService(
            new AiRequestLogWriter(
                repository,
                new AiCostEstimator(pricingRepository ?? new InMemoryPricingRepository([])),
                new FakeUserContext(),
                Options.Create(applicationOptions ?? new ApplicationOptions()),
                Options.Create(new AiRequestLoggingOptions
                {
                    FailureMode = failureMode
                }),
                NullLogger<AiRequestLogWriter>.Instance),
            TimeProvider.System,
            NullLogger<AiModelRequestLoggingService>.Instance);
    }

    private sealed class SuccessfulModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new AiModelResponse(
                "answer",
                request.Model,
                "fake",
                new AiModelUsage(10, 5, 15),
                request.CorrelationId));
        }
    }

    private sealed class ThrowingModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            throw new AiModelException(
                "fake",
                "rate limit",
                "rate_limited",
                HttpStatusCode.TooManyRequests);
        }
    }

    private sealed class CancelingModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException("model request canceled");
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

    private sealed class CapturingUsageRepository : IUsageRepository
    {
        public UsageQuery? Query { get; private set; }

        public Task<UsageSummary> GetUsageAsync(
            UsageQuery query,
            CancellationToken cancellationToken)
        {
            Query = query;
            return Task.FromResult(new UsageSummary(
                Requests: 1,
                InputTokens: 1,
                OutputTokens: 1,
                EmbeddingTokens: 0,
                EstimatedCost: 0.01m,
                Currency: "USD"));
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

    private sealed class FakeUserContext : IUserContext
    {
        public bool IsAuthenticated => true;

        public string? UserId => "alice";

        public string? TenantId => "tenant-a";

        public IReadOnlyCollection<string> Roles { get; } = [];

        public IReadOnlyCollection<string> Groups { get; } = [];
    }
}
