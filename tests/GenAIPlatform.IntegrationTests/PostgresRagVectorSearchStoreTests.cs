using System.Globalization;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed partial class PostgresRagVectorSearchStoreTests(
    PostgresRepositoryFixture postgres)
{
    [Fact]
    public async Task SearchAsync_TranslatesMissingConnectionStringToRetrievalException()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "MissingConnection"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var store = serviceProvider.GetRequiredService<IRagVectorSearchStore>();

        var exception = await Assert.ThrowsAsync<RagVectorSearchException>(() =>
            store.SearchAsync(
                new RagVectorSearchQuery(
                    [1f, 0f],
                    "test-embedding",
                    "test-provider",
                    "tenant-a",
                    "alice",
                    TopK: 5,
                    MinSimilarityScore: 0.2,
                    DocumentIds: []),
                TestContext.Current.CancellationToken));

        Assert.Equal("postgres", exception.Provider);
        Assert.Equal("retrieval_unavailable", exception.ErrorCode);
        Assert.Equal("RAG retrieval store is not configured.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task SearchAsync_TranslatesMalformedConnectionStringToRetrievalException()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = "Host=localhost;Port=not-a-number;Username=genai;Password=secret",
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var store = serviceProvider.GetRequiredService<IRagVectorSearchStore>();

        var exception = await Assert.ThrowsAsync<RagVectorSearchException>(() =>
            store.SearchAsync(
                new RagVectorSearchQuery(
                    [1f, 0f],
                    "test-embedding",
                    "test-provider",
                    "tenant-a",
                    "alice",
                    TopK: 5,
                    MinSimilarityScore: 0.2,
                    DocumentIds: []),
                TestContext.Current.CancellationToken));

        Assert.Equal("postgres", exception.Provider);
        Assert.Equal("retrieval_unavailable", exception.ErrorCode);
        Assert.Equal("RAG retrieval store is unavailable.", exception.Message);
        Assert.NotNull(exception.InnerException);
        Assert.Equal(
            "PostgreSQL connection string 'GenAIPlatform' is invalid.",
            exception.InnerException.Message);
        Assert.Null(exception.InnerException.InnerException);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckReadinessAsync_TranslatesMalformedConnectionStringToRetrievalException()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = "Host=localhost;Port=not-a-number;Username=genai;Password=secret",
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var store = serviceProvider.GetRequiredService<IRagVectorSearchStore>();

        var exception = await Assert.ThrowsAsync<RagVectorSearchException>(() =>
            store.CheckReadinessAsync(TestContext.Current.CancellationToken));

        Assert.Equal("postgres", exception.Provider);
        Assert.Equal("retrieval_unavailable", exception.ErrorCode);
        Assert.Equal("RAG retrieval store is unavailable.", exception.Message);
        Assert.NotNull(exception.InnerException);
        Assert.Equal(
            "PostgreSQL connection string 'GenAIPlatform' is invalid.",
            exception.InnerException.Message);
        Assert.Null(exception.InnerException.InnerException);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("zero")]
    [InlineData("nan")]
    [InlineData("positive-infinity")]
    [InlineData("negative-infinity")]
    public async Task SearchAsync_RejectsInvalidQueryEmbeddingAsRetrievalFailure(
        string vectorShape)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = "Host=localhost;Port=5432;Database=unused;Username=genai;Password=secret",
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var store = serviceProvider.GetRequiredService<IRagVectorSearchStore>();
        IReadOnlyList<float> queryEmbedding = vectorShape switch
        {
            "empty" => [],
            "zero" => [0f, 0f],
            "nan" => [float.NaN, 1f],
            "positive-infinity" => [float.PositiveInfinity, 1f],
            "negative-infinity" => [float.NegativeInfinity, 1f],
            _ => throw new InvalidOperationException($"Unknown vector shape '{vectorShape}'.")
        };

        var exception = await Assert.ThrowsAsync<RagVectorSearchException>(() =>
            store.SearchAsync(
                new RagVectorSearchQuery(
                    queryEmbedding,
                    "test-embedding",
                    "test-provider",
                    "tenant-a",
                    "alice",
                    TopK: 5,
                    MinSimilarityScore: 0.2,
                    DocumentIds: []),
                TestContext.Current.CancellationToken));

        Assert.Equal("postgres", exception.Provider);
        Assert.Equal("retrieval_query_failed", exception.ErrorCode);
        Assert.Equal("RAG retrieval query embedding is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    [InlineData(int.MaxValue)]
    public async Task SearchAsync_RejectsInvalidTopKBeforeSqlExecution(
        int topK)
    {
        using var serviceProvider = CreateUnavailableStoreServices();
        var store = serviceProvider.GetRequiredService<IRagVectorSearchStore>();

        var exception = await Assert.ThrowsAsync<RagVectorSearchException>(() =>
            store.SearchAsync(
                new RagVectorSearchQuery(
                    [1f, 0f],
                    "test-embedding",
                    "test-provider",
                    "tenant-a",
                    "alice",
                    TopK: topK,
                    MinSimilarityScore: 0.2,
                    DocumentIds: []),
                TestContext.Current.CancellationToken));

        Assert.Equal("postgres", exception.Provider);
        Assert.Equal("retrieval_query_failed", exception.ErrorCode);
        Assert.Equal("RAG retrieval query result limit is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.01)]
    [InlineData(1.01)]
    public async Task SearchAsync_RejectsInvalidMinSimilarityScoreBeforeSqlExecution(
        double minSimilarityScore)
    {
        using var serviceProvider = CreateUnavailableStoreServices();
        var store = serviceProvider.GetRequiredService<IRagVectorSearchStore>();

        var exception = await Assert.ThrowsAsync<RagVectorSearchException>(() =>
            store.SearchAsync(
                new RagVectorSearchQuery(
                    [1f, 0f],
                    "test-embedding",
                    "test-provider",
                    "tenant-a",
                    "alice",
                    TopK: 5,
                    MinSimilarityScore: minSimilarityScore,
                    DocumentIds: []),
                TestContext.Current.CancellationToken));

        Assert.Equal("postgres", exception.Provider);
        Assert.Equal("retrieval_query_failed", exception.ErrorCode);
        Assert.Equal("RAG retrieval query similarity threshold is invalid.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("null-query", "RAG retrieval query is required.")]
    [InlineData("blank-tenant", "RAG retrieval query tenant is invalid.")]
    [InlineData("blank-model", "RAG retrieval query embedding model is invalid.")]
    [InlineData("blank-provider", "RAG retrieval query embedding provider is invalid.")]
    [InlineData("null-document-ids", "RAG retrieval query document filter is invalid.")]
    [InlineData("empty-document-id", "RAG retrieval query document filter is invalid.")]
    [InlineData("too-many-document-ids", "RAG retrieval query document filter has too many values.")]
    public async Task SearchAsync_RejectsInvalidQueryMetadataBeforeSqlExecution(
        string queryShape,
        string expectedMessage)
    {
        using var serviceProvider = CreateUnavailableStoreServices();
        var store = serviceProvider.GetRequiredService<IRagVectorSearchStore>();
        var query = queryShape switch
        {
            "null-query" => null,
            "blank-tenant" => CreateValidSearchQuery(tenantId: " "),
            "blank-model" => CreateValidSearchQuery(embeddingModel: " "),
            "blank-provider" => CreateValidSearchQuery(embeddingProvider: " "),
            "null-document-ids" => new RagVectorSearchQuery(
                [1f, 0f],
                "test-embedding",
                "test-provider",
                "tenant-a",
                "alice",
                TopK: 5,
                MinSimilarityScore: 0.2,
                DocumentIds: null!),
            "empty-document-id" => CreateValidSearchQuery(documentIds: [Guid.Empty]),
            "too-many-document-ids" => CreateValidSearchQuery(
                documentIds: Enumerable
                    .Range(0, RagVectorSearchQuery.MaxDocumentFilters + 1)
                    .Select(_ => Guid.NewGuid())
                    .ToArray()),
            _ => throw new InvalidOperationException($"Unknown query shape '{queryShape}'.")
        };

        var exception = await Assert.ThrowsAsync<RagVectorSearchException>(() =>
            store.SearchAsync(query!, TestContext.Current.CancellationToken));

        Assert.Equal("postgres", exception.Provider);
        Assert.Equal("retrieval_query_failed", exception.ErrorCode);
        Assert.Equal(expectedMessage, exception.Message);
        Assert.Null(exception.InnerException);
    }

    private async Task<SearchScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
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
        return new SearchScope(
            serviceProvider,
            connectionString,
            serviceProvider.GetRequiredService<IDocumentMetadataRepository>(),
            serviceProvider.GetRequiredService<IIndexingJobRepository>(),
            serviceProvider.GetRequiredService<IRagVectorSearchStore>());
    }

    private static ServiceProvider CreateUnavailableStoreServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = "Host=localhost;Port=5432;Database=unused;Username=genai;Password=secret",
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }

    private static RagVectorSearchQuery CreateValidSearchQuery(
        string embeddingModel = "test-embedding",
        string embeddingProvider = "test-provider",
        string tenantId = "tenant-a",
        IReadOnlyCollection<Guid>? documentIds = null)
    {
        return new RagVectorSearchQuery(
            [1f, 0f],
            embeddingModel,
            embeddingProvider,
            tenantId,
            "alice",
            TopK: 5,
            MinSimilarityScore: 0.2,
            DocumentIds: documentIds ?? []);
    }

    private static async Task CleanDatabaseAsync(string connectionString)
    {
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            TRUNCATE TABLE
                genai.document_chunks,
                genai.indexing_jobs,
                genai.documents;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ResetGenAiSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DROP SCHEMA IF EXISTS genai CASCADE;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Document> CreateIndexedDocumentAsync(
        SearchScope scope,
        string tenantId,
        string ownerUserId,
        DocumentAccessLevel accessLevel,
        string title,
        IReadOnlyList<float> embedding,
        DateTimeOffset now,
        string embeddingModel = "test-embedding",
        string embeddingProvider = "test-provider",
        int documentVersion = 1)
    {
        var document = CreateDocument(
            tenantId,
            ownerUserId,
            accessLevel,
            title,
            now,
            documentVersion);
        var job = CreatePendingJob(document.Id, now);

        await scope.Metadata.CreateDocumentWithJobAsync(
            document,
            job,
            TestContext.Current.CancellationToken);
        var claimed = await scope.Jobs.ClaimNextPendingJobAsync(
            $"worker-{document.Id:n}",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);

        Assert.NotNull(claimed);
        Assert.Equal(document.Id, claimed.DocumentId);

        var completed = await scope.Jobs.ReplaceChunksAndCompleteIndexingAsync(
            document,
            claimed,
            [CreateChunk(document, title, embedding, now, embeddingModel, embeddingProvider)],
            TestContext.Current.CancellationToken);

        Assert.True(completed);
        return document;
    }

    private static Document CreateDocument(
        string tenantId,
        string ownerUserId,
        DocumentAccessLevel accessLevel,
        string title,
        DateTimeOffset now,
        int version = 1)
    {
        return new Document(
            Guid.NewGuid(),
            tenantId,
            ownerUserId,
            $"{title.Replace(' ', '-').ToLowerInvariant()}.md",
            title,
            "text/markdown",
            ".md",
            $"memory://{Guid.NewGuid():n}/notes.md",
            SizeBytes: 32,
            new string('a', 64),
            version,
            accessLevel,
            DocumentIndexingStatus.PendingIndexing,
            now,
            now,
            FailureReason: null);
    }

    private static IndexingJob CreatePendingJob(
        Guid documentId,
        DateTimeOffset now)
    {
        return new IndexingJob(
            Guid.NewGuid(),
            documentId,
            IndexingJobStatus.Pending,
            Attempts: 0,
            MaxAttempts: 3,
            now,
            now,
            AvailableAtUtc: now,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            WorkerId: null,
            FailureReason: null);
    }

    private static DocumentChunk CreateChunk(
        Document document,
        string title,
        IReadOnlyList<float> embedding,
        DateTimeOffset now,
        string embeddingModel,
        string embeddingProvider)
    {
        return new DocumentChunk(
            Guid.NewGuid(),
            document.Id,
            document.Version,
            Position: 0,
            $"Document text for {title}.",
            new string('b', 64),
            ApproximateTokenCount: 4,
            "test-profile",
            "v-test",
            embedding,
            embeddingModel,
            embeddingProvider,
            EmbeddingInputTokens: 4,
            now);
    }

    private static async Task InsertHistoricalChunkAsync(
        string connectionString,
        Document document,
        int documentVersion,
        IReadOnlyList<float> embedding,
        DateTimeOffset now)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.document_chunks (
                id, document_id, document_version, position, text, text_hash,
                approximate_token_count, chunking_profile, chunking_profile_version,
                embedding_model, embedding_provider, embedding_dimensions, embedding_input_tokens,
                embedding_vector, created_at_utc)
            VALUES (
                @id, @document_id, @document_version, @position, @text, @text_hash,
                @approximate_token_count, @chunking_profile, @chunking_profile_version,
                @embedding_model, @embedding_provider, @embedding_dimensions, @embedding_input_tokens,
                @embedding_vector::vector, @created_at_utc);
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("document_id", document.Id);
        command.Parameters.AddWithValue("document_version", documentVersion);
        command.Parameters.AddWithValue("position", 0);
        command.Parameters.AddWithValue("text", "Historical version text.");
        command.Parameters.AddWithValue("text_hash", new string('c', 64));
        command.Parameters.AddWithValue("approximate_token_count", 3);
        command.Parameters.AddWithValue("chunking_profile", "test-profile");
        command.Parameters.AddWithValue("chunking_profile_version", "v-test");
        command.Parameters.AddWithValue("embedding_model", "test-embedding");
        command.Parameters.AddWithValue("embedding_provider", "test-provider");
        command.Parameters.AddWithValue("embedding_dimensions", embedding.Count);
        command.Parameters.AddWithValue("embedding_input_tokens", 3);
        command.Parameters.AddWithValue("embedding_vector", ToVectorText(embedding));
        command.Parameters.AddWithValue("created_at_utc", now);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertLegacyIndexedChunkAsync(
        string connectionString,
        Guid documentId,
        Guid chunkId,
        IReadOnlyList<float> embedding,
        DateTimeOffset now)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.documents (
                id, tenant_id, owner_user_id, file_name, title, content_type, source_extension,
                storage_path, size_bytes, content_hash, version, access_level, indexing_status,
                created_at_utc, updated_at_utc, failure_reason)
            VALUES (
                @document_id, 'tenant-a', 'alice', 'legacy-notes.md', 'Legacy Notes', 'text/markdown', '.md',
                @storage_path, 64, @content_hash, 1, 'Private', 'Indexed',
                @created_at_utc, @updated_at_utc, NULL);

            INSERT INTO genai.indexing_jobs (
                id, document_id, status, attempts, max_attempts, created_at_utc, updated_at_utc,
                available_at_utc, started_at_utc, completed_at_utc, worker_id, failure_reason)
            VALUES (
                @job_id, @document_id, 'Completed', 1, 3, @created_at_utc, @updated_at_utc,
                @created_at_utc, @created_at_utc, @created_at_utc, 'migration-test-worker', NULL);

            INSERT INTO genai.document_chunks (
                id, document_id, document_version, position, text, text_hash,
                approximate_token_count, chunking_profile, chunking_profile_version,
                embedding_model, embedding_provider, embedding_dimensions, embedding_input_tokens,
                embedding_values, created_at_utc)
            VALUES (
                @chunk_id, @document_id, 1, 0, 'Legacy vectorized content.', @text_hash,
                4, 'test-profile', 'v-test',
                'test-embedding', 'test-provider', @embedding_dimensions, 4,
                @embedding_values, @created_at_utc);
            """, connection);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("chunk_id", chunkId);
        command.Parameters.AddWithValue("job_id", Guid.NewGuid());
        command.Parameters.AddWithValue("storage_path", $"memory://{documentId:n}/legacy-notes.md");
        command.Parameters.AddWithValue("content_hash", new string('d', 64));
        command.Parameters.AddWithValue("text_hash", new string('e', 64));
        command.Parameters.AddWithValue("embedding_dimensions", embedding.Count);
        command.Parameters.AddWithValue("embedding_values", embedding.ToArray());
        command.Parameters.AddWithValue("created_at_utc", now);
        command.Parameters.AddWithValue("updated_at_utc", now);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<ChunkVectorState> ReadChunkVectorStateAsync(
        string connectionString,
        Guid chunkId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                embedding_vector IS NOT NULL,
                vector_dims(embedding_vector)
            FROM genai.document_chunks
            WHERE id = @chunk_id;
            """, connection);
        command.Parameters.AddWithValue("chunk_id", chunkId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Expected migrated document chunk was not found.");
        }

        return new ChunkVectorState(
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1));
    }

    private static string ToVectorText(IReadOnlyList<float> embedding)
    {
        return "[" + string.Join(",", embedding.Select(static value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";
    }

    private static float[] CreateUnitVector(int dimensions)
    {
        var vector = new float[dimensions];
        vector[0] = 1f;
        return vector;
    }

    /// <summary>
    /// Proves the dimension CHECK from 0002 still guards the surviving pgvector column after the
    /// relational copy and its cardinality CHECK were dropped by 0007.
    /// </summary>
    private static async Task AssertMismatchedVectorDimensionsAreRejectedAsync(
        string connectionString,
        Document document)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.document_chunks (
                id, document_id, document_version, position, text, text_hash,
                approximate_token_count, chunking_profile, chunking_profile_version,
                embedding_model, embedding_provider, embedding_dimensions, embedding_input_tokens,
                embedding_vector, created_at_utc)
            VALUES (
                @id, @document_id, @document_version, 99, 'Dimension mismatch.', @text_hash,
                3, 'test-profile', 'v-test', 'shared-model', 'shared-provider', 24, 3,
                '[1,0]'::vector, @created_at_utc);
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("document_id", document.Id);
        command.Parameters.AddWithValue("document_version", document.Version);
        command.Parameters.AddWithValue("text_hash", new string('f', 64));
        command.Parameters.AddWithValue("created_at_utc", DateTimeOffset.Parse("2026-05-13T12:00:00Z"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            command.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            "ck_document_chunks_embedding_vector_dimensions",
            exception.ConstraintName);
    }

    private sealed record ChunkVectorState(
        bool HasEmbeddingVector,
        int? VectorDimensions);

    private sealed record SearchScope(
        ServiceProvider Services,
        string ConnectionString,
        IDocumentMetadataRepository Metadata, IIndexingJobRepository Jobs,
        IRagVectorSearchStore VectorSearchStore)
        : IDisposable
    {
        public void Dispose() => Services.Dispose();
    }
}
