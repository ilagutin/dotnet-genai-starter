using GenAIPlatform.Application.Knowledge.Retrieval;

namespace GenAIPlatform.IntegrationTests;

public sealed partial class PostgresRagVectorSearchStoreTests
{
    [DockerAvailableFact]
    public async Task Applying003_BackfillsLegacyEmbeddingValuesAndMakesChunkSearchable()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        var now = DateTimeOffset.Parse("2026-05-13T12:00:00Z");
        var documentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var chunkId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        try
        {
            await ResetGenAiSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
                connectionString,
                "001-enable-pgvector.sql",
                "002-document-ingestion.sql");
            await InsertLegacyIndexedChunkAsync(
                connectionString,
                documentId,
                chunkId,
                [1f, 0f],
                now);

            await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
                connectionString,
                "003-pgvector-retrieval.sql");

            var vectorState = await ReadChunkVectorStateAsync(connectionString, chunkId);
            Assert.True(vectorState.HasEmbeddingVector);
            Assert.Equal(2, vectorState.VectorDimensions);

            using var scope = await CreateScopeAsync();
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

            var result = Assert.Single(results);
            Assert.Equal(documentId, result.DocumentId);
            Assert.Equal(chunkId, result.ChunkId);
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Applying003_SkipsInvalidLegacyEmbeddingValuesAndKeepsValidChunksSearchable()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        var now = DateTimeOffset.Parse("2026-05-13T12:00:00Z");
        var validDocumentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var validChunkId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var nanChunkId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var positiveInfinityChunkId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var negativeInfinityChunkId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        try
        {
            await ResetGenAiSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
                connectionString,
                "001-enable-pgvector.sql",
                "002-document-ingestion.sql");
            await InsertLegacyIndexedChunkAsync(
                connectionString,
                validDocumentId,
                validChunkId,
                [1f, 0f],
                now);
            await InsertLegacyIndexedChunkAsync(
                connectionString,
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                nanChunkId,
                [float.NaN, 1f],
                now.AddSeconds(1));
            await InsertLegacyIndexedChunkAsync(
                connectionString,
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                positiveInfinityChunkId,
                [float.PositiveInfinity, 1f],
                now.AddSeconds(2));
            await InsertLegacyIndexedChunkAsync(
                connectionString,
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                negativeInfinityChunkId,
                [float.NegativeInfinity, 1f],
                now.AddSeconds(3));

            await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
                connectionString,
                "003-pgvector-retrieval.sql");
            await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
                connectionString,
                "003-pgvector-retrieval.sql");

            var validVectorState = await ReadChunkVectorStateAsync(connectionString, validChunkId);
            Assert.True(validVectorState.HasEmbeddingVector);
            Assert.Equal(2, validVectorState.VectorDimensions);

            foreach (var invalidChunkId in new[]
                     {
                         nanChunkId,
                         positiveInfinityChunkId,
                         negativeInfinityChunkId
                     })
            {
                var invalidVectorState = await ReadChunkVectorStateAsync(connectionString, invalidChunkId);
                Assert.False(invalidVectorState.HasEmbeddingVector);
                Assert.Null(invalidVectorState.VectorDimensions);
            }

            using var scope = await CreateScopeAsync();
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
            Assert.Equal(validDocumentId, result.DocumentId);
            Assert.Equal(validChunkId, result.ChunkId);
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

}
