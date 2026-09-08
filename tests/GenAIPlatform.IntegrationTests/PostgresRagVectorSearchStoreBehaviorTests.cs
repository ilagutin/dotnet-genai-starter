using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Documents;

namespace GenAIPlatform.IntegrationTests;

public sealed partial class PostgresRagVectorSearchStoreTests
{
    [DockerAvailableFact]
    public async Task SearchAsync_AppliesTenantAccessAndDocumentFiltersBeforeReturningChunks()
    {
        using var scope = await CreateScopeAsync();
        await CleanDatabaseAsync(scope.ConnectionString);

        var now = DateTimeOffset.Parse("2026-05-13T12:00:00Z");
        var alicePrivate = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Alice Private",
            [1f, 0f],
            now);
        var bobPrivate = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "bob",
            DocumentAccessLevel.Private,
            "Bob Private",
            [1f, 0f],
            now.AddSeconds(1));
        var tenantPublic = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "bob",
            DocumentAccessLevel.TenantPublic,
            "Tenant Public",
            [0.9f, 0.1f],
            now.AddSeconds(2));
        var otherTenantPublic = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-b",
            ownerUserId: "mallory",
            DocumentAccessLevel.TenantPublic,
            "Other Tenant",
            [1f, 0f],
            now.AddSeconds(3));

        var results = await scope.VectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                [1f, 0f],
                "test-embedding",
                "test-provider",
                "tenant-a",
                "alice",
                TopK: 10,
                MinSimilarityScore: 0.2,
                DocumentIds: []),
            TestContext.Current.CancellationToken);
        var filteredResults = await scope.VectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                [1f, 0f],
                "test-embedding",
                "test-provider",
                "tenant-a",
                "alice",
                TopK: 10,
                MinSimilarityScore: 0.2,
                DocumentIds: [tenantPublic.Id]),
            TestContext.Current.CancellationToken);

        Assert.Contains(results, result => result.DocumentId == alicePrivate.Id);
        Assert.Contains(results, result => result.DocumentId == tenantPublic.Id);
        Assert.DoesNotContain(results, result => result.DocumentId == bobPrivate.Id);
        Assert.DoesNotContain(results, result => result.DocumentId == otherTenantPublic.Id);
        Assert.Equal(alicePrivate.Id, results[0].DocumentId);
        Assert.True(results[0].SimilarityScore >= results[1].SimilarityScore);

        var onlyFilteredDocument = Assert.Single(filteredResults);
        Assert.Equal(tenantPublic.Id, onlyFilteredDocument.DocumentId);
    }

    [DockerAvailableFact]
    public async Task SearchAsync_RespectsMinimumSimilarity()
    {
        using var scope = await CreateScopeAsync();
        await CleanDatabaseAsync(scope.ConnectionString);

        var now = DateTimeOffset.Parse("2026-05-13T12:00:00Z");
        await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Low Similarity",
            [0f, 1f],
            now);

        var results = await scope.VectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                [1f, 0f],
                "test-embedding",
                "test-provider",
                "tenant-a",
                "alice",
                TopK: 5,
                MinSimilarityScore: 0.2,
                DocumentIds: []),
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [DockerAvailableFact]
    public async Task SearchAsync_UsesDeterministicTieBreakersForEqualSimilarity()
    {
        using var scope = await CreateScopeAsync();
        await CleanDatabaseAsync(scope.ConnectionString);

        var now = DateTimeOffset.Parse("2026-05-13T12:00:00Z");
        var newerDocument = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Newer Tie",
            [1f, 0f],
            now.AddMinutes(1));
        var olderDocument = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Older Tie",
            [1f, 0f],
            now);

        var results = await scope.VectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                [1f, 0f],
                "test-embedding",
                "test-provider",
                "tenant-a",
                "alice",
                TopK: 10,
                MinSimilarityScore: 0.2,
                DocumentIds: []),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { olderDocument.Id, newerDocument.Id },
            results.Select(static result => result.DocumentId).ToArray());
        Assert.All(results, result => Assert.Equal(1.0, result.SimilarityScore, precision: 12));
    }

    [DockerAvailableFact]
    public async Task SearchAsync_FiltersEmbeddingProviderAndModelBeforeRanking()
    {
        using var scope = await CreateScopeAsync();
        await CleanDatabaseAsync(scope.ConnectionString);

        var now = DateTimeOffset.Parse("2026-05-13T12:00:00Z");
        var compatible = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Compatible",
            [0.8f, 0.2f],
            now,
            embeddingModel: "embedding-model-a",
            embeddingProvider: "provider-a");
        var incompatibleProvider = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Incompatible Provider",
            [1f, 0f],
            now.AddSeconds(1),
            embeddingModel: "embedding-model-a",
            embeddingProvider: "provider-b");
        var incompatibleModel = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Incompatible Model",
            [1f, 0f],
            now.AddSeconds(2),
            embeddingModel: "embedding-model-b",
            embeddingProvider: "provider-a");

        var results = await scope.VectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                [1f, 0f],
                "embedding-model-a",
                "provider-a",
                "tenant-a",
                "alice",
                TopK: 10,
                MinSimilarityScore: 0.2,
                DocumentIds: []),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(compatible.Id, result.DocumentId);
        Assert.DoesNotContain(results, item => item.DocumentId == incompatibleProvider.Id);
        Assert.DoesNotContain(results, item => item.DocumentId == incompatibleModel.Id);
    }

    [DockerAvailableFact]
    public async Task SearchAsync_FiltersMismatchedEmbeddingDimensionsBeforeRanking()
    {
        using var scope = await CreateScopeAsync();
        await CleanDatabaseAsync(scope.ConnectionString);

        var now = DateTimeOffset.Parse("2026-05-13T12:00:00Z");
        var compatible = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Compatible Sixteen Dimensions",
            CreateUnitVector(dimensions: 16),
            now,
            embeddingModel: "shared-model",
            embeddingProvider: "shared-provider");
        var mismatchedDimensions = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Mismatched Two Dimensions",
            [1f, 0f],
            now.AddSeconds(1),
            embeddingModel: "shared-model",
            embeddingProvider: "shared-provider");

        var results = await scope.VectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                CreateUnitVector(dimensions: 16),
                "shared-model",
                "shared-provider",
                "tenant-a",
                "alice",
                TopK: 10,
                MinSimilarityScore: 0.2,
                DocumentIds: []),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(compatible.Id, result.DocumentId);
        Assert.DoesNotContain(results, item => item.DocumentId == mismatchedDimensions.Id);
    }

    [DockerAvailableFact]
    public async Task SearchAsync_ExcludesZeroMagnitudeStoredEmbeddingWithoutReturningNaN()
    {
        using var scope = await CreateScopeAsync();
        await CleanDatabaseAsync(scope.ConnectionString);

        var now = DateTimeOffset.Parse("2026-05-13T12:00:00Z");
        var compatible = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Compatible Nonzero",
            [1f, 0f],
            now,
            embeddingModel: "shared-model",
            embeddingProvider: "shared-provider");
        var zeroMagnitude = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Zero Magnitude",
            [0f, 0f],
            now.AddSeconds(1),
            embeddingModel: "shared-model",
            embeddingProvider: "shared-provider");

        var results = await scope.VectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                [1f, 0f],
                "shared-model",
                "shared-provider",
                "tenant-a",
                "alice",
                TopK: 10,
                MinSimilarityScore: 0.2,
                DocumentIds: []),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(compatible.Id, result.DocumentId);
        Assert.False(double.IsNaN(result.SimilarityScore));
        Assert.DoesNotContain(results, item => item.DocumentId == zeroMagnitude.Id);
    }

    [DockerAvailableFact]
    public async Task SearchAsync_ReturnsOnlyCurrentDocumentVersionChunks()
    {
        using var scope = await CreateScopeAsync();
        await CleanDatabaseAsync(scope.ConnectionString);

        var now = DateTimeOffset.Parse("2026-05-13T12:00:00Z");
        var document = await CreateIndexedDocumentAsync(
            scope,
            tenantId: "tenant-a",
            ownerUserId: "alice",
            DocumentAccessLevel.Private,
            "Versioned Notes",
            [0.6f, 0.8f],
            now,
            documentVersion: 2);
        await InsertHistoricalChunkAsync(
            scope.ConnectionString,
            document,
            documentVersion: 1,
            embedding: [1f, 0f],
            now.AddSeconds(-10));

        var results = await scope.VectorSearchStore.SearchAsync(
            new RagVectorSearchQuery(
                [1f, 0f],
                "test-embedding",
                "test-provider",
                "tenant-a",
                "alice",
                TopK: 10,
                MinSimilarityScore: 0.2,
                DocumentIds: []),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal(document.Id, result.DocumentId);
        Assert.Equal(2, result.DocumentVersion);
    }

    [DockerAvailableFact]
    public async Task CheckReadinessAsync_RejectsLegacySchemaBeforeSearch()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await ResetGenAiSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
                connectionString,
                "001-enable-pgvector.sql",
                "002-document-ingestion.sql");

            using var scope = await CreateScopeAsync();
            var exception = await Assert.ThrowsAsync<RagVectorSearchException>(() =>
                scope.VectorSearchStore.CheckReadinessAsync(TestContext.Current.CancellationToken));

            Assert.Equal("postgres", exception.Provider);
            Assert.Equal("retrieval_schema_error", exception.ErrorCode);
            Assert.Equal("RAG retrieval schema is not ready.", exception.Message);
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

}
