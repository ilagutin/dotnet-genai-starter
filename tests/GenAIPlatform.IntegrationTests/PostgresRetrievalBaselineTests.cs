using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Evaluations.Retrieval;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresRetrievalBaselineTests(PostgresRepositoryFixture postgres)
{
    private const string UntouchedTenant = "tenant-outside-baseline";

    [DockerAvailableFact]
    public async Task RetrievalBaseline_MeetsItsFrozenGatesOnTheLexicalMock()
    {
        using var scope = await CreateScopeAsync();
        await SeedUnrelatedTenantDocumentAsync(scope.ConnectionString);

        var report = await RunAsync(scope);

        Assert.True(
            report.GatesPassed,
            $"gates: {string.Join(", ", report.Gates.Select(gate => $"{gate.Name}={gate.Actual}"))}");
        Assert.Equal("retrieval-baseline-v1", report.DatasetVersion);
        Assert.Equal("mock-lexical", report.Configuration.EmbeddingProvider);
        Assert.Equal("Lexical", report.Configuration.MockVariant);
        Assert.Equal(1024, report.Configuration.EmbeddingDimensions);
        Assert.Equal(3, report.Configuration.TopK);
        Assert.Equal(1, report.Aggregates.RecallAtK);
        Assert.Equal(1, report.Aggregates.MeanReciprocalRank);
        Assert.Equal(1, report.Aggregates.NoMatchAccuracy);
        Assert.All(report.Gates, gate => Assert.True(gate.Met, gate.Name));
        Assert.Equal(
            RetrievalBaselineCategories.All.Order(StringComparer.Ordinal),
            report.Aggregates.QueryCountsByCategory.Keys.Order(StringComparer.Ordinal));
    }

    [DockerAvailableFact]
    public async Task RetrievalBaseline_NeverReturnsDocumentsTheCallerMayNotRead()
    {
        using var scope = await CreateScopeAsync();

        var report = await RunAsync(scope);

        AssertNeverRetrieved(report, RetrievalBaselineCategories.TenantIsolation, "doc-alpine-glacier");
        AssertNeverRetrieved(report, RetrievalBaselineCategories.PrivateOwnership, "doc-basalt-quarry");
        AssertNeverRetrieved(report, RetrievalBaselineCategories.EmbeddingCompatibility, "doc-kiln-firing");
        AssertNeverRetrieved(report, RetrievalBaselineCategories.EmbeddingCompatibility, "doc-anemometer-array");
        Assert.All(report.Queries, query => Assert.DoesNotContain("doc-kiln-firing", query.RetrievedDocumentIds));
        Assert.All(report.Queries, query => Assert.DoesNotContain("doc-anemometer-array", query.RetrievedDocumentIds));

        var isolationLeak = QueryReport(report, "q-tenant-isolation-001");
        var ownershipLeak = QueryReport(report, "q-private-ownership-001");
        var compatibilityLeak = QueryReport(report, "q-embedding-compatibility-001");
        Assert.Empty(QueryReport(report, "q-embedding-compatibility-003").RetrievedDocumentIds);
        var legacyChunkQuery = QueryReport(report, "q-document-version-003");

        Assert.Empty(isolationLeak.RetrievedDocumentIds);
        Assert.Empty(ownershipLeak.RetrievedDocumentIds);
        Assert.Empty(compatibilityLeak.RetrievedDocumentIds);
        Assert.Empty(legacyChunkQuery.RetrievedDocumentIds);

        Assert.Equal(["doc-alpine-glacier"], QueryReport(report, "q-tenant-isolation-003").RetrievedDocumentIds);
        Assert.Equal(["doc-basalt-quarry"], QueryReport(report, "q-private-ownership-003").RetrievedDocumentIds);
        Assert.Contains("doc-lighthouse-optics", QueryReport(report, "q-document-version-001").RetrievedDocumentIds);
    }

    [DockerAvailableFact]
    public async Task RetrievalBaseline_ProducesIdenticalQualityOnRepeatedRuns()
    {
        using var scope = await CreateScopeAsync();

        var first = await RunAsync(scope);
        var second = await RunAsync(scope);

        Assert.Equal(first.DatasetHash, second.DatasetHash);
        Assert.Equal(first.Configuration.SettingsHash, second.Configuration.SettingsHash);
        Assert.Equal(first.Aggregates.RecallAtK, second.Aggregates.RecallAtK);
        Assert.Equal(first.Aggregates.MeanReciprocalRank, second.Aggregates.MeanReciprocalRank);
        Assert.Equal(first.Aggregates.NoMatchAccuracy, second.Aggregates.NoMatchAccuracy);
        Assert.Equal(first.Aggregates.RecallEligibleQueryCount, second.Aggregates.RecallEligibleQueryCount);
        Assert.Equal(
            first.Aggregates.HitCountsByCategory.OrderBy(entry => entry.Key, StringComparer.Ordinal),
            second.Aggregates.HitCountsByCategory.OrderBy(entry => entry.Key, StringComparer.Ordinal));
        Assert.Equal(
            first.Queries.Select(Quality).ToArray(),
            second.Queries.Select(Quality).ToArray());
    }

    [DockerAvailableFact]
    public async Task RetrievalBaseline_LeavesDocumentsOutsideTheBenchmarkTenantsUntouched()
    {
        using var scope = await CreateScopeAsync();
        var documentId = await SeedUnrelatedTenantDocumentAsync(scope.ConnectionString);

        await RunAsync(scope);

        Assert.True(await DocumentExistsAsync(scope.ConnectionString, documentId));
    }

    [DockerAvailableFact]
    public async Task RetrievalBaseline_RefusesACorpusNamingATenantOutsideTheBenchmarkNamespace()
    {
        using var scope = await CreateScopeAsync();
        var documentId = await SeedUnrelatedTenantDocumentAsync(scope.ConnectionString);
        using var serviceScope = scope.Services.CreateScope();
        var store = serviceScope.ServiceProvider.GetRequiredService<IRetrievalBaselineCorpusStore>();

        var exception = await Assert.ThrowsAsync<RetrievalBaselineStoreException>(
            () => store.ReplaceCorpusAsync(
                new RetrievalBaselineCorpus([UntouchedTenant], []),
                TestContext.Current.CancellationToken));

        Assert.Contains(
            "outside the fixed benchmark tenant namespace",
            exception.Message,
            StringComparison.Ordinal);
        Assert.True(await DocumentExistsAsync(scope.ConnectionString, documentId));
    }

    [DockerAvailableFact]
    public async Task RetrievalBaseline_FailsTheRecallGateWhenRetrievalRanksTheWrongDocumentsFirst()
    {
        using var scope = await CreateScopeAsync();
        using var regressed = await CreateScopeAsync(store =>
            new LowestSimilarityFirstSearchStore(store));

        var healthy = await RunAsync(scope);
        var report = await RunAsync(regressed);

        Assert.True(healthy.GatesPassed);
        Assert.False(report.GatesPassed);
        var recallGate = Assert.Single(
            report.Gates,
            gate => gate.Name == RetrievalBaselineGateResult.RecallGateName);
        Assert.False(recallGate.Met);
        Assert.True(
            report.Aggregates.RecallAtK < recallGate.Threshold,
            $"recall was {report.Aggregates.RecallAtK}");
    }

    [DockerAvailableFact]
    public async Task RetrievalBaseline_ReportsStoreVersionsWithoutConnectionDetails()
    {
        using var scope = await CreateScopeAsync();

        var report = await RunAsync(scope);

        Assert.False(string.IsNullOrWhiteSpace(report.Environment.PostgresVersion));
        Assert.False(string.IsNullOrWhiteSpace(report.Environment.PgvectorVersion));
        Assert.DoesNotContain("Password", report.Environment.PostgresVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static (string, string, double?, int?, bool) Quality(RetrievalBaselineQueryReport report)
    {
        return (
            report.QueryId,
            string.Join(",", report.RetrievedDocumentIds),
            report.RecallAtK,
            report.FirstRelevantRank,
            report.Hit);
    }

    private static RetrievalBaselineQueryReport QueryReport(RetrievalBaselineReport report, string queryId)
    {
        return Assert.Single(report.Queries, query => query.QueryId == queryId);
    }

    private static void AssertNeverRetrieved(
        RetrievalBaselineReport report,
        string category,
        string forbiddenDocumentId)
    {
        var forbiddenQueries = report.Queries
            .Where(query => query.Category == category && query.NoMatch)
            .ToArray();

        Assert.NotEmpty(forbiddenQueries);
        foreach (var query in forbiddenQueries)
        {
            Assert.DoesNotContain(forbiddenDocumentId, query.RetrievedDocumentIds);
        }
    }

    private static async Task<RetrievalBaselineReport> RunAsync(BaselineScope scope)
    {
        using var serviceScope = scope.Services.CreateScope();
        var dispatcher = serviceScope.ServiceProvider.GetRequiredService<IApplicationDispatcher>();

        return await dispatcher.DispatchAsync<RunRetrievalBaselineCommand, RetrievalBaselineReport>(
            new RunRetrievalBaselineCommand(DatasetVersion: null, CodeRevision: "integration-test"),
            TestContext.Current.CancellationToken);
    }

    private async Task<BaselineScope> CreateScopeAsync(
        Func<IRagVectorSearchStore, IRagVectorSearchStore>? decorateSearchStore = null)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = connectionString,
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform",
                ["GenAIPlatform:Embeddings:MockVariant"] = "Lexical",
                ["GenAIPlatform:Embeddings:MockDimensions"] = "1024"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        BaselineScope? inner = null;
        if (decorateSearchStore is not null)
        {
            inner = await CreateScopeAsync();
            var innerServices = inner.Services;
            services.AddSingleton<IRagVectorSearchStore>(_ => decorateSearchStore(
                innerServices.GetRequiredService<IRagVectorSearchStore>()));
        }

        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        return new BaselineScope(services.BuildServiceProvider(), connectionString, inner);
    }

    private static async Task<Guid> SeedUnrelatedTenantDocumentAsync(string connectionString)
    {
        var documentId = Guid.Parse("f1c0dd11-2222-4333-8444-555566667777");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.documents (
                id, tenant_id, owner_user_id, file_name, title, content_type, source_extension,
                storage_path, size_bytes, content_hash, version, access_level, indexing_status,
                created_at_utc, updated_at_utc, failure_reason)
            VALUES (
                @id, @tenant_id, 'outsider', 'outside.md', 'Outside', 'text/markdown', '.md',
                'memory://outside/outside.md', 32, @content_hash, 1, 'TenantPublic', 'Indexed',
                now(), now(), NULL)
            ON CONFLICT (id) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("id", documentId);
        command.Parameters.AddWithValue("tenant_id", UntouchedTenant);
        command.Parameters.AddWithValue("content_hash", new string('a', 64));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return documentId;
    }

    private static async Task<bool> DocumentExistsAsync(string connectionString, Guid documentId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM genai.documents WHERE id = @id);",
            connection);
        command.Parameters.AddWithValue("id", documentId);

        return (bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? false);
    }

    /// <summary>
    /// A deliberately regressed retrieval store: it widens the candidate set and then
    /// returns the least similar chunks first, which is what a relevance regression looks
    /// like from the outside.
    /// </summary>
    private sealed class LowestSimilarityFirstSearchStore(IRagVectorSearchStore inner) : IRagVectorSearchStore
    {
        public Task CheckReadinessAsync(CancellationToken cancellationToken)
        {
            return inner.CheckReadinessAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
            RagVectorSearchQuery query,
            CancellationToken cancellationToken)
        {
            var widened = await inner.SearchAsync(
                query with { TopK = RagVectorSearchQuery.MaxTopK, MinSimilarityScore = -1 },
                cancellationToken);

            return widened
                .OrderBy(static chunk => chunk.SimilarityScore)
                .ThenBy(static chunk => chunk.ChunkId)
                .Take(query.TopK)
                .ToArray();
        }
    }

    private sealed record BaselineScope(
        ServiceProvider Services,
        string ConnectionString,
        BaselineScope? Inner = null) : IDisposable
    {
        public void Dispose()
        {
            Services.Dispose();
            Inner?.Dispose();
        }
    }
}
