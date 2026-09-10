using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresDocumentStorageCleanupRepositoryTests(
    PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task CleanupWorkflow_SeparateScopesRecordsClaimsAndDeletesStorage()
    {
        var rootPath = CreateStorageRootPath();
        try
        {
            using var provider = await CreateServiceProviderAsync(rootPath);
            await CleanDatabaseAsync(provider.ConnectionString);
            var documentId = Guid.NewGuid();
            string? physicalStoragePath = null;

            using (var apiScope = provider.Services.CreateScope())
            {
                var stored = await SaveCommittedDocumentAsync(
                    apiScope.ServiceProvider,
                    documentId,
                    "api orphan content");
                await apiScope.ServiceProvider
                    .GetRequiredService<IDocumentStorageCleanupRepository>()
                    .RecordAsync(CreateCleanupRequest(documentId, stored), TestContext.Current.CancellationToken);

                physicalStoragePath = GetPhysicalPath(rootPath, stored.StoragePath);
                Assert.True(File.Exists(physicalStoragePath));
            }

            using (var workerScope = provider.Services.CreateScope())
            {
                var dispatcher = workerScope.ServiceProvider.GetRequiredService<IApplicationDispatcher>();
                var response = await dispatcher.DispatchAsync<ProcessDocumentStorageCleanupCommand, ProcessDocumentStorageCleanupResponse>(
                    new ProcessDocumentStorageCleanupCommand("worker-1", MaxRequests: 10),
                    TestContext.Current.CancellationToken);

                Assert.Equal(1, response.Discovered);
                Assert.Equal(1, response.Deleted);
                Assert.Equal(0, response.Deferred);
                Assert.Equal(0, response.Failed);
            }

            var record = await ReadCleanupRecordAsync(provider.ConnectionString, documentId);
            Assert.Equal(DocumentStorageCleanupStatus.Completed.ToString(), record.Status);
            Assert.Null(record.WorkerId);
            Assert.False(File.Exists(physicalStoragePath));
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
        }
    }

    [DockerAvailableFact]
    public async Task ClaimBatchAsync_TwoWorkersRacingClaimsRequestOnce()
    {
        var rootPath = CreateStorageRootPath();
        try
        {
            using var provider = await CreateServiceProviderAsync(rootPath);
            await CleanDatabaseAsync(provider.ConnectionString);
            var documentId = Guid.NewGuid();

            using (var scope = provider.Services.CreateScope())
            {
                var stored = await SaveCommittedDocumentAsync(
                    scope.ServiceProvider,
                    documentId,
                    "racing content");
                await scope.ServiceProvider
                    .GetRequiredService<IDocumentStorageCleanupRepository>()
                    .RecordAsync(CreateCleanupRequest(documentId, stored), TestContext.Current.CancellationToken);
            }

            using var firstScope = provider.Services.CreateScope();
            using var secondScope = provider.Services.CreateScope();
            var firstRepository = firstScope.ServiceProvider.GetRequiredService<IDocumentStorageCleanupRepository>();
            var secondRepository = secondScope.ServiceProvider.GetRequiredService<IDocumentStorageCleanupRepository>();

            var claims = await Task.WhenAll(
                firstRepository.ClaimBatchAsync("worker-1", 1, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken),
                secondRepository.ClaimBatchAsync("worker-2", 1, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken));

            Assert.Equal(1, claims.Sum(static batch => batch.Count));
            Assert.Contains(claims, static batch => batch.Count == 1);
            Assert.Contains(claims, static batch => batch.Count == 0);

            var record = await ReadCleanupRecordAsync(provider.ConnectionString, documentId);
            Assert.Equal(DocumentStorageCleanupStatus.Processing.ToString(), record.Status);
            Assert.True(record.WorkerId is "worker-1" or "worker-2");
            Assert.Equal(1, record.Attempts);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
        }
    }

    [DockerAvailableFact]
    public async Task CompleteAsync_IsIdempotent()
    {
        var rootPath = CreateStorageRootPath();
        try
        {
            using var provider = await CreateServiceProviderAsync(rootPath);
            await CleanDatabaseAsync(provider.ConnectionString);
            var documentId = Guid.NewGuid();

            using var scope = provider.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDocumentStorageCleanupRepository>();
            var stored = await SaveCommittedDocumentAsync(scope.ServiceProvider, documentId, "complete content");
            await repository.RecordAsync(CreateCleanupRequest(documentId, stored), TestContext.Current.CancellationToken);

            var claimed = Assert.Single(await repository.ClaimBatchAsync(
                "worker-1",
                1,
                TimeSpan.FromMinutes(5),
                TestContext.Current.CancellationToken));

            Assert.True(await repository.CompleteAsync(claimed, TestContext.Current.CancellationToken));
            Assert.True(await repository.CompleteAsync(claimed, TestContext.Current.CancellationToken));

            var record = await ReadCleanupRecordAsync(provider.ConnectionString, documentId);
            Assert.Equal(DocumentStorageCleanupStatus.Completed.ToString(), record.Status);
            Assert.Null(record.WorkerId);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
        }
    }

    [DockerAvailableFact]
    public async Task CleanupWorkflow_MetadataExistsDefersAndDoesNotDeleteStorage()
    {
        var rootPath = CreateStorageRootPath();
        try
        {
            using var provider = await CreateServiceProviderAsync(rootPath);
            await CleanDatabaseAsync(provider.ConnectionString);
            var documentId = Guid.NewGuid();

            using (var scope = provider.Services.CreateScope())
            {
                var stored = await SaveCommittedDocumentAsync(scope.ServiceProvider, documentId, "metadata exists");
                await CreateDocumentMetadataAsync(scope.ServiceProvider, documentId, stored);
                await scope.ServiceProvider
                    .GetRequiredService<IDocumentStorageCleanupRepository>()
                    .RecordAsync(CreateCleanupRequest(documentId, stored), TestContext.Current.CancellationToken);
            }

            using (var workerScope = provider.Services.CreateScope())
            {
                var dispatcher = workerScope.ServiceProvider.GetRequiredService<IApplicationDispatcher>();
                var response = await dispatcher.DispatchAsync<ProcessDocumentStorageCleanupCommand, ProcessDocumentStorageCleanupResponse>(
                    new ProcessDocumentStorageCleanupCommand("worker-1", MaxRequests: 10),
                    TestContext.Current.CancellationToken);

                Assert.Equal(1, response.Discovered);
                Assert.Equal(0, response.Deleted);
                Assert.Equal(1, response.Deferred);
                Assert.Equal(0, response.Failed);
            }

            var record = await ReadCleanupRecordAsync(provider.ConnectionString, documentId);
            Assert.Equal(DocumentStorageCleanupStatus.Deferred.ToString(), record.Status);
            Assert.Equal("Document metadata still exists.", record.FailureReason);
            Assert.True(Directory.EnumerateFiles(rootPath).Any());
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
        }
    }

    [DockerAvailableFact]
    public async Task CleanupWorkflow_InvalidProofMarksFailedAndDoesNotDeleteStorage()
    {
        var rootPath = CreateStorageRootPath();
        try
        {
            using var provider = await CreateServiceProviderAsync(rootPath);
            await CleanDatabaseAsync(provider.ConnectionString);
            var documentId = Guid.NewGuid();

            using (var scope = provider.Services.CreateScope())
            {
                var stored = await SaveCommittedDocumentAsync(scope.ServiceProvider, documentId, "invalid proof");
                await scope.ServiceProvider
                    .GetRequiredService<IDocumentStorageCleanupRepository>()
                    .RecordAsync(
                        CreateCleanupRequest(documentId, stored, metadataAbsenceProof: "InvalidProof"),
                        TestContext.Current.CancellationToken);
            }

            using (var workerScope = provider.Services.CreateScope())
            {
                var dispatcher = workerScope.ServiceProvider.GetRequiredService<IApplicationDispatcher>();
                var response = await dispatcher.DispatchAsync<ProcessDocumentStorageCleanupCommand, ProcessDocumentStorageCleanupResponse>(
                    new ProcessDocumentStorageCleanupCommand("worker-1", MaxRequests: 10),
                    TestContext.Current.CancellationToken);

                Assert.Equal(1, response.Discovered);
                Assert.Equal(0, response.Deleted);
                Assert.Equal(0, response.Deferred);
                Assert.Equal(1, response.Failed);
            }

            var record = await ReadCleanupRecordAsync(provider.ConnectionString, documentId);
            Assert.Equal(DocumentStorageCleanupStatus.Failed.ToString(), record.Status);
            Assert.Equal("Metadata absence proof is invalid.", record.FailureReason);
            Assert.True(Directory.EnumerateFiles(rootPath).Any());
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
        }
    }

    private async Task<ProviderScope> CreateServiceProviderAsync(string rootPath)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = connectionString,
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform",
                ["GenAIPlatform:DocumentStorage:RootPath"] = rootPath,
                ["GenAIPlatform:DocumentIngestion:StorageCleanupRetryDelaySeconds"] = "60",
                ["GenAIPlatform:DocumentIngestion:ProcessingJobLeaseSeconds"] = "300"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        return new ProviderScope(
            services.BuildServiceProvider(),
            connectionString);
    }

    private static async Task<StoredDocument> SaveCommittedDocumentAsync(
        IServiceProvider services,
        Guid documentId,
        string content)
    {
        var storage = services.GetRequiredService<IDocumentStorage>();
        var stored = await storage.SaveAsync(
            documentId,
            "cleanup.md",
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
            maxSizeBytes: 1024,
            TestContext.Current.CancellationToken);
        await storage.CommitAsync(stored, TestContext.Current.CancellationToken);
        return stored;
    }

    private static async Task CreateDocumentMetadataAsync(
        IServiceProvider services,
        Guid documentId,
        StoredDocument stored)
    {
        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        var document = new Document(
            documentId,
            "tenant-a",
            "alice",
            "cleanup.md",
            "Cleanup",
            "text/markdown",
            ".md",
            stored.StoragePath,
            stored.SizeBytes,
            stored.ContentHash,
            Version: 1,
            DocumentAccessLevel.Private,
            DocumentIndexingStatus.PendingIndexing,
            now,
            now,
            FailureReason: null);
        var indexingJob = new IndexingJob(
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

        await services
            .GetRequiredService<IDocumentMetadataRepository>()
            .CreateDocumentWithJobAsync(document, indexingJob, TestContext.Current.CancellationToken);
    }

    private static DocumentStorageCleanupRequest CreateCleanupRequest(
        Guid documentId,
        StoredDocument stored,
        string metadataAbsenceProof = nameof(DocumentMetadataNotCommittedException))
    {
        return new DocumentStorageCleanupRequest(
            documentId,
            stored.StoragePath,
            stored.StagedStoragePath,
            stored.ContentHash,
            stored.SizeBytes,
            metadataAbsenceProof,
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            "IOException");
    }

    private static async Task<CleanupRecord> ReadCleanupRecordAsync(
        string connectionString,
        Guid documentId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT status, worker_id, attempts, failure_reason
            FROM genai.document_storage_cleanup_requests
            WHERE document_id = @document_id;
            """, connection);
        command.Parameters.AddWithValue("document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Cleanup request was not found.");
        }

        return new CleanupRecord(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task CleanDatabaseAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            TRUNCATE TABLE
                genai.document_storage_cleanup_requests,
                genai.document_chunks,
                genai.indexing_jobs,
                genai.documents;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateStorageRootPath()
    {
        return Path.Combine(Path.GetTempPath(), $"genai-cleanup-tests-{Guid.NewGuid():n}");
    }

    private static string GetPhysicalPath(
        string rootPath,
        string storagePath)
    {
        return Path.Combine(rootPath, storagePath);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record ProviderScope(
        ServiceProvider Services,
        string ConnectionString)
        : IDisposable
    {
        public void Dispose()
        {
            Services.Dispose();
        }
    }

    private sealed record CleanupRecord(
        string Status,
        string? WorkerId,
        int Attempts,
        string? FailureReason);
}
