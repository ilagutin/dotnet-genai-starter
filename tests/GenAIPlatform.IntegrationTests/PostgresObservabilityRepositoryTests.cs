using GenAIPlatform.Application.Usage.GetUsage;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Domain.Prompts;
using GenAIPlatform.Infrastructure;
using GenAIPlatform.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresObservabilityRepositoryTests(
    PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task RequestLogPersistence_StoresSanitizedMetadataAndUsage()
    {
        using var scope = await CreateScopeAsync();
        await CleanObservabilityTablesAsync(scope.ConnectionString);
        await InsertPricingAsync(
            scope.ConnectionString,
            "fake",
            "test-model",
            inputPrice: 10m,
            outputPrice: 20m,
            DateTimeOffset.Parse("2026-05-01T00:00:00Z"));
        var repository = scope.Services.GetRequiredService<IAiRequestLogRepository>();

        var entry = new AiRequestLogEntry(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "v1",
            "alice",
            "tenant-a",
            "correlation-1",
            "fake",
            "test-model",
            "Succeeded",
            ErrorCode: null,
            TimeSpan.FromMilliseconds(42),
            InputTokens: 1000,
            OutputTokens: 2000,
            TotalTokens: 3000,
            EmbeddingTokens: null,
            EstimatedCost: 0.05m,
            CostCurrency: "USD",
            new PromptMetadata("rag-chat", "v1", new string('b', 64)),
            TimeSpan.FromMilliseconds(12),
            [new RetrievedDocumentReference("1", Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"))],
            DateTimeOffset.Parse("2026-05-15T12:00:00Z"));

        await repository.AddAsync(entry, TestContext.Current.CancellationToken);

        var persisted = await ReadPersistedLogAsync(scope.ConnectionString, entry.RequestId);
        Assert.Equal("v1", persisted.ApiVersion);
        Assert.Equal("alice", persisted.UserId);
        Assert.Equal("tenant-a", persisted.TenantId);
        Assert.Equal("fake", persisted.Provider);
        Assert.Equal("test-model", persisted.Model);
        Assert.Equal("Succeeded", persisted.Status);
        Assert.Equal(42, persisted.LatencyMs);
        Assert.Equal("rag-chat", persisted.PromptTemplateName);
        Assert.Equal(new string('b', 64), persisted.PromptTemplateContentHash);
        Assert.Equal(12, persisted.RetrievalLatencyMs);
        Assert.Equal([Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")], persisted.DocumentIds);
        Assert.Equal(["1"], persisted.CitationReferences);
    }

    [DockerAvailableFact]
    public async Task PricingAndUsage_UseHistoricalPricingAndFilters()
    {
        using var scope = await CreateScopeAsync();
        await CleanObservabilityTablesAsync(scope.ConnectionString);
        await InsertPricingAsync(
            scope.ConnectionString,
            "fake",
            "test-model",
            inputPrice: 1m,
            outputPrice: 2m,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-01T00:00:00Z"));
        await InsertPricingAsync(
            scope.ConnectionString,
            "fake",
            "test-model",
            inputPrice: 10m,
            outputPrice: 20m,
            DateTimeOffset.Parse("2026-05-01T00:00:00Z"));
        var pricingRepository = scope.Services.GetRequiredService<IPricingRepository>();
        var requestLogRepository = scope.Services.GetRequiredService<IAiRequestLogRepository>();
        var usageRepository = scope.Services.GetRequiredService<IUsageRepository>();

        var historicalPricing = await pricingRepository.GetEffectivePricingAsync(
            "fake",
            "test-model",
            DateTimeOffset.Parse("2026-04-15T00:00:00Z"),
            TestContext.Current.CancellationToken);
        var currentPricing = await pricingRepository.GetEffectivePricingAsync(
            "fake",
            "test-model",
            DateTimeOffset.Parse("2026-05-15T00:00:00Z"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(historicalPricing);
        Assert.Equal(1m, historicalPricing.InputTokenPricePerMillion);
        Assert.NotNull(currentPricing);
        Assert.Equal(10m, currentPricing.InputTokenPricePerMillion);

        await requestLogRepository.AddAsync(
            CreateLog(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "alice",
                "tenant-a",
                "test-model",
                100,
                20,
                0.0014m,
                DateTimeOffset.Parse("2026-05-10T00:00:00Z")),
            TestContext.Current.CancellationToken);
        await requestLogRepository.AddAsync(
            CreateLog(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                "bob",
                "tenant-a",
                "other-model",
                500,
                100,
                0.007m,
                DateTimeOffset.Parse("2026-05-11T00:00:00Z")),
            TestContext.Current.CancellationToken);

        var usage = await usageRepository.GetUsageAsync(
            new UsageQuery(
                DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-05-31T23:59:59Z"),
                UserId: "alice",
                TenantId: "tenant-a",
                Model: "test-model"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, usage.Requests);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Equal(0.0014m, usage.EstimatedCost);
        Assert.Equal("USD", usage.Currency);
    }

    [DockerAvailableFact]
    public async Task Usage_AllowsOmittedOptionalFilters()
    {
        using var scope = await CreateScopeAsync();
        await CleanObservabilityTablesAsync(scope.ConnectionString);
        var requestLogRepository = scope.Services.GetRequiredService<IAiRequestLogRepository>();
        var usageRepository = scope.Services.GetRequiredService<IUsageRepository>();

        await requestLogRepository.AddAsync(
            CreateLog(
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                "alice",
                "tenant-a",
                "test-model",
                100,
                20,
                0.0014m,
                DateTimeOffset.Parse("2026-05-10T00:00:00Z")),
            TestContext.Current.CancellationToken);

        var usage = await usageRepository.GetUsageAsync(
            new UsageQuery(
                DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-05-31T23:59:59Z"),
                UserId: null,
                TenantId: "tenant-a",
                Model: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, usage.Requests);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Equal(0.0014m, usage.EstimatedCost);
        Assert.Equal("USD", usage.Currency);
    }

    [DockerAvailableFact]
    public async Task Usage_RejectsAggregatingMultipleCostCurrencies()
    {
        using var scope = await CreateScopeAsync();
        await CleanObservabilityTablesAsync(scope.ConnectionString);
        var requestLogRepository = scope.Services.GetRequiredService<IAiRequestLogRepository>();
        var usageRepository = scope.Services.GetRequiredService<IUsageRepository>();

        await requestLogRepository.AddAsync(
            CreateLog(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                "alice",
                "tenant-a",
                "test-model",
                100,
                20,
                0.0014m,
                DateTimeOffset.Parse("2026-05-10T00:00:00Z"),
                "USD"),
            TestContext.Current.CancellationToken);
        await requestLogRepository.AddAsync(
            CreateLog(
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                "alice",
                "tenant-a",
                "test-model",
                500,
                100,
                0.007m,
                DateTimeOffset.Parse("2026-05-11T00:00:00Z"),
                "EUR"),
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<UsageQueryValidationException>(() =>
            usageRepository.GetUsageAsync(
                new UsageQuery(
                    DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
                    DateTimeOffset.Parse("2026-05-31T23:59:59Z"),
                    UserId: "alice",
                    TenantId: "tenant-a",
                    Model: "test-model"),
                TestContext.Current.CancellationToken));

        Assert.Contains("multiple cost currencies", exception.Message);
    }

    private async Task<RepositoryScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = connectionString,
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        var serviceProvider = services.BuildServiceProvider();

        return new RepositoryScope(serviceProvider, connectionString);
    }

    private static AiRequestLogEntry CreateLog(
        Guid requestId,
        string userId,
        string tenantId,
        string model,
        int inputTokens,
        int outputTokens,
        decimal estimatedCost,
        DateTimeOffset createdAtUtc,
        string costCurrency = "USD")
    {
        return new AiRequestLogEntry(
            requestId,
            "v1",
            userId,
            tenantId,
            $"correlation-{requestId:N}",
            "fake",
            model,
            "Succeeded",
            ErrorCode: null,
            TimeSpan.FromMilliseconds(1),
            inputTokens,
            outputTokens,
            inputTokens + outputTokens,
            EmbeddingTokens: null,
            estimatedCost,
            costCurrency,
            Prompt: null,
            RetrievalLatency: null,
            RetrievedDocuments: [],
            createdAtUtc);
    }

    private static async Task CleanObservabilityTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            TRUNCATE TABLE
                genai.ai_request_logs,
                genai.ai_model_pricing;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertPricingAsync(
        string connectionString,
        string provider,
        string model,
        decimal inputPrice,
        decimal outputPrice,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveToUtc = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.ai_model_pricing (
                id, provider, model, currency, input_token_price_per_million,
                output_token_price_per_million, embedding_token_price_per_million,
                effective_from_utc, effective_to_utc)
            VALUES (
                @id, @provider, @model, 'USD', @input_price,
                @output_price, NULL, @effective_from_utc, @effective_to_utc);
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("model", model);
        command.Parameters.AddWithValue("input_price", inputPrice);
        command.Parameters.AddWithValue("output_price", outputPrice);
        command.Parameters.AddWithValue("effective_from_utc", effectiveFromUtc);
        command.Parameters.AddWithValue("effective_to_utc", effectiveToUtc ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PersistedLog> ReadPersistedLogAsync(
        string connectionString,
        Guid requestId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT api_version, user_id, tenant_id, provider, model, status,
                   latency_ms, prompt_template_name, prompt_template_content_hash,
                   retrieval_latency_ms, retrieved_document_ids, citation_references
            FROM genai.ai_request_logs
            WHERE request_id = @request_id;
            """, connection);
        command.Parameters.AddWithValue("request_id", requestId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("AI request log was not found.");
        }

        return new PersistedLog(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.GetFieldValue<Guid[]>(10),
            reader.GetFieldValue<string[]>(11));
    }

    private sealed record RepositoryScope(
        ServiceProvider Services,
        string ConnectionString)
        : IDisposable
    {
        public void Dispose()
        {
            Services.Dispose();
        }
    }

    private sealed record PersistedLog(
        string ApiVersion,
        string UserId,
        string TenantId,
        string Provider,
        string Model,
        string Status,
        int LatencyMs,
        string PromptTemplateName,
        string PromptTemplateContentHash,
        int RetrievalLatencyMs,
        Guid[] DocumentIds,
        string[] CitationReferences);
}
