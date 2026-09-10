using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed partial class PostgresDocumentIngestionRepositoryTests(
    PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task ClaimNextPendingJobAsync_PreventsDuplicateClaimsAndCompletesAtomically()
    {
        using var scope = await CreateRepositoryScopeAsync();
        await CleanDatabaseAsync(scope);

        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        var document = CreateDocument(
            tenantId: "tenant-a",
            ownerUserId: "alice",
            accessLevel: DocumentAccessLevel.Private,
            now);
        var indexingJob = CreatePendingJob(document.Id, maxAttempts: 3, now);

        await scope.Metadata.CreateDocumentWithJobAsync(
            document,
            indexingJob,
            TestContext.Current.CancellationToken);

        var claimed = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-1",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);
        var duplicateClaim = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-2",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);

        Assert.NotNull(claimed);
        Assert.Equal(IndexingJobStatus.Processing, claimed.Status);
        Assert.Equal(1, claimed.Attempts);
        Assert.Equal("worker-1", claimed.WorkerId);
        Assert.Null(duplicateClaim);

        var completed = await scope.Jobs.ReplaceChunksAndCompleteIndexingAsync(
            document,
            claimed,
            [
                CreateChunk(document, position: 0, text: "first chunk", now),
                CreateChunk(document, position: 1, text: "second chunk", now)
            ],
            TestContext.Current.CancellationToken);

        Assert.True(completed);

        var ownerStatus = await scope.Metadata.GetDocumentStatusAsync(
            document.Id,
            "tenant-a",
            "alice",
            TestContext.Current.CancellationToken);
        var otherUserStatus = await scope.Metadata.GetDocumentStatusAsync(
            document.Id,
            "tenant-a",
            "bob",
            TestContext.Current.CancellationToken);
        var otherTenantStatus = await scope.Metadata.GetDocumentStatusAsync(
            document.Id,
            "tenant-b",
            "alice",
            TestContext.Current.CancellationToken);

        Assert.NotNull(ownerStatus);
        Assert.Equal(DocumentIndexingStatus.Indexed, ownerStatus.Document.IndexingStatus);
        Assert.Equal(IndexingJobStatus.Completed, ownerStatus.LatestJob?.Status);
        Assert.Equal(2, ownerStatus.ChunkCount);
        Assert.Null(otherUserStatus);
        Assert.Null(otherTenantStatus);
    }

    [DockerAvailableFact]
    public async Task GetDocumentStatusAsync_AllowsSameTenantPublicDocument()
    {
        using var scope = await CreateRepositoryScopeAsync();
        await CleanDatabaseAsync(scope);

        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        var document = CreateDocument(
            tenantId: "tenant-a",
            ownerUserId: "alice",
            accessLevel: DocumentAccessLevel.TenantPublic,
            now);
        var indexingJob = CreatePendingJob(document.Id, maxAttempts: 3, now);

        await scope.Metadata.CreateDocumentWithJobAsync(
            document,
            indexingJob,
            TestContext.Current.CancellationToken);

        var sameTenantStatus = await scope.Metadata.GetDocumentStatusAsync(
            document.Id,
            "tenant-a",
            "bob",
            TestContext.Current.CancellationToken);
        var otherTenantStatus = await scope.Metadata.GetDocumentStatusAsync(
            document.Id,
            "tenant-b",
            "bob",
            TestContext.Current.CancellationToken);

        Assert.NotNull(sameTenantStatus);
        Assert.Equal(document.Id, sameTenantStatus.Document.Id);
        Assert.Null(otherTenantStatus);
    }

    [DockerAvailableFact]
    public async Task ProcessingJob_ReclaimedJobRejectsStaleWorkerCompletionAndFailure()
    {
        using var scope = await CreateRepositoryScopeAsync();
        await CleanDatabaseAsync(scope);

        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        var document = CreateDocument(
            tenantId: "tenant-a",
            ownerUserId: "alice",
            accessLevel: DocumentAccessLevel.Private,
            now);
        var indexingJob = CreatePendingJob(document.Id, maxAttempts: 3, now);

        await scope.Metadata.CreateDocumentWithJobAsync(
            document,
            indexingJob,
            TestContext.Current.CancellationToken);

        var firstClaim = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-1",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);
        var reclaimed = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-2",
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.NotNull(firstClaim);
        Assert.NotNull(reclaimed);
        Assert.Equal(2, reclaimed.Attempts);
        Assert.Equal("worker-2", reclaimed.WorkerId);

        var staleCompletion = await scope.Jobs.ReplaceChunksAndCompleteIndexingAsync(
            document,
            firstClaim,
            [CreateChunk(document, position: 0, text: "stale chunk", now)],
            TestContext.Current.CancellationToken);
        var staleFailure = await scope.Jobs.MarkIndexingFailedAsync(
            document.Id,
            firstClaim,
            "stale failure",
            retry: false,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        var currentFailure = await scope.Jobs.MarkIndexingFailedAsync(
            document.Id,
            reclaimed,
            "current failure",
            retry: false,
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        var status = await scope.Metadata.GetDocumentStatusAsync(
            document.Id,
            "tenant-a",
            "alice",
            TestContext.Current.CancellationToken);

        Assert.False(staleCompletion);
        Assert.False(staleFailure);
        Assert.True(currentFailure);
        Assert.NotNull(status);
        Assert.Equal(DocumentIndexingStatus.Failed, status.Document.IndexingStatus);
        Assert.Equal(IndexingJobStatus.Failed, status.LatestJob?.Status);
        Assert.Equal("current failure", status.Document.FailureReason);
    }

    [DockerAvailableFact]
    public async Task ProcessingLease_UsesDatabaseClockSoSkewedCallerTimesCannotReclaimActiveJob()
    {
        using var scope = await CreateRepositoryScopeAsync();
        await CleanDatabaseAsync(scope);

        var skewedAppClock = DateTimeOffset.Parse("2020-01-01T12:00:00Z");
        var document = CreateDocument(
            tenantId: "tenant-a",
            ownerUserId: "alice",
            accessLevel: DocumentAccessLevel.Private,
            skewedAppClock);
        var indexingJob = CreatePendingJob(document.Id, maxAttempts: 3, skewedAppClock);

        await scope.Metadata.CreateDocumentWithJobAsync(
            document,
            indexingJob,
            TestContext.Current.CancellationToken);

        var firstClaim = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-1",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);

        Assert.NotNull(firstClaim);

        var renewed = await scope.Jobs.RenewProcessingLeaseAsync(
            document.Id,
            firstClaim,
            TestContext.Current.CancellationToken);
        var reclaimAttempt = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-2",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);
        var ownership = await ReadJobOwnershipAsync(scope.ConnectionString, firstClaim.Id);

        Assert.True(renewed);
        Assert.Null(reclaimAttempt);
        Assert.Equal(IndexingJobStatus.Processing.ToString(), ownership.Status);
        Assert.Equal("worker-1", ownership.WorkerId);
    }

    [DockerAvailableFact]
    public async Task MarkIndexingFailedAsync_RetryDelaysNextClaimAndPreservesAttempts()
    {
        using var scope = await CreateRepositoryScopeAsync();
        await CleanDatabaseAsync(scope);

        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        var document = CreateDocument(
            tenantId: "tenant-a",
            ownerUserId: "alice",
            accessLevel: DocumentAccessLevel.Private,
            now);
        var indexingJob = CreatePendingJob(document.Id, maxAttempts: 3, now);

        await scope.Metadata.CreateDocumentWithJobAsync(
            document,
            indexingJob,
            TestContext.Current.CancellationToken);

        var firstClaim = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-1",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);

        Assert.NotNull(firstClaim);

        var retryRecorded = await scope.Jobs.MarkIndexingFailedAsync(
            document.Id,
            firstClaim,
            "temporary failure",
            retry: true,
            TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken);
        var pendingJob = await ReadJobOwnershipAsync(scope.ConnectionString, firstClaim.Id);
        var unavailableClaim = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-2",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        var retryClaim = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-2",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);

        Assert.True(retryRecorded);
        Assert.Equal(IndexingJobStatus.Pending.ToString(), pendingJob.Status);
        Assert.Null(pendingJob.WorkerId);
        Assert.Null(pendingJob.StartedAtUtc);
        Assert.Null(pendingJob.CompletedAtUtc);
        Assert.Null(unavailableClaim);
        Assert.NotNull(retryClaim);
        Assert.Equal(2, retryClaim.Attempts);
        Assert.Equal(3, retryClaim.MaxAttempts);
        Assert.Equal("worker-2", retryClaim.WorkerId);
    }

    [DockerAvailableFact]
    public async Task MarkExpiredIndexingJobsFailedAsync_ReturnsCleanupCountAndMarksDocumentFailed()
    {
        using var scope = await CreateRepositoryScopeAsync();
        await CleanDatabaseAsync(scope);

        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        var document = CreateDocument(
            tenantId: "tenant-a",
            ownerUserId: "alice",
            accessLevel: DocumentAccessLevel.Private,
            now);
        var exhaustedJob = CreatePendingJob(document.Id, maxAttempts: 1, now) with
        {
            Attempts = 1
        };

        await scope.Metadata.CreateDocumentWithJobAsync(
            document,
            exhaustedJob,
            TestContext.Current.CancellationToken);

        var cleanupCount = await scope.Jobs.MarkExpiredIndexingJobsFailedAsync(
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);
        var status = await scope.Metadata.GetDocumentStatusAsync(
            document.Id,
            "tenant-a",
            "alice",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, cleanupCount);
        Assert.NotNull(status);
        Assert.Equal(DocumentIndexingStatus.Failed, status.Document.IndexingStatus);
        Assert.Equal(IndexingJobStatus.Failed, status.LatestJob?.Status);
        Assert.Equal("Indexing job reached maximum attempts.", status.Document.FailureReason);
    }

    [DockerAvailableFact]
    public async Task MarkExpiredIndexingJobsFailedAsync_MarksExpiredProcessingJobAndDocumentFailed()
    {
        using var scope = await CreateRepositoryScopeAsync();
        await CleanDatabaseAsync(scope);

        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        var document = CreateDocument(
            tenantId: "tenant-a",
            ownerUserId: "alice",
            accessLevel: DocumentAccessLevel.Private,
            now);
        var indexingJob = CreatePendingJob(document.Id, maxAttempts: 1, now);

        await scope.Metadata.CreateDocumentWithJobAsync(
            document,
            indexingJob,
            TestContext.Current.CancellationToken);
        var processingJob = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-1",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);

        Assert.NotNull(processingJob);

        var cleanupCount = await scope.Jobs.MarkExpiredIndexingJobsFailedAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        var ownership = await ReadJobOwnershipAsync(scope.ConnectionString, processingJob.Id);
        var status = await scope.Metadata.GetDocumentStatusAsync(
            document.Id,
            "tenant-a",
            "alice",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, cleanupCount);
        Assert.Equal(IndexingJobStatus.Failed.ToString(), ownership.Status);
        Assert.Equal("worker-1", ownership.WorkerId);
        Assert.Equal(1, ownership.Attempts);
        Assert.NotNull(ownership.CompletedAtUtc);
        Assert.NotNull(status);
        Assert.Equal(DocumentIndexingStatus.Failed, status.Document.IndexingStatus);
        Assert.Equal(IndexingJobStatus.Failed, status.LatestJob?.Status);
        Assert.Equal("Indexing job timed out while processing.", status.Document.FailureReason);
    }

    [DockerAvailableFact]
    public async Task ReleaseProcessingJobAndRefundAttemptAsync_RequeuesOwnedJobWithoutConsumingAttempt()
    {
        using var scope = await CreateRepositoryScopeAsync();
        await CleanDatabaseAsync(scope);

        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        var document = CreateDocument(
            tenantId: "tenant-a",
            ownerUserId: "alice",
            accessLevel: DocumentAccessLevel.Private,
            now);
        var indexingJob = CreatePendingJob(document.Id, maxAttempts: 3, now);

        await scope.Metadata.CreateDocumentWithJobAsync(
            document,
            indexingJob,
            TestContext.Current.CancellationToken);

        var firstClaim = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-1",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);

        Assert.NotNull(firstClaim);

        var released = await scope.Jobs.ReleaseProcessingJobAndRefundAttemptAsync(
            document.Id,
            firstClaim,
            TestContext.Current.CancellationToken);
        var retryClaim = await scope.Jobs.ClaimNextPendingJobAsync(
            "worker-2",
            TimeSpan.FromHours(1),
            TestContext.Current.CancellationToken);

        Assert.True(released);
        Assert.NotNull(retryClaim);
        Assert.Equal(1, retryClaim.Attempts);
        Assert.Equal("worker-2", retryClaim.WorkerId);
    }

    [DockerAvailableFact]
    public async Task Repository_WorksAgainstSchemaCreatedFromInitScript()
    {
        using var firstScope = await CreateRepositoryScopeAsync();
        await CleanDatabaseAsync(firstScope);

        var alternateConnectionString = await CreateAlternateDatabaseConnectionStringAsync();
        using var secondScope = await CreateRepositoryScopeAsync(alternateConnectionString);
        await EnsureSchemaAsync(secondScope.ConnectionString);
        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        var document = CreateDocument(
            tenantId: "tenant-a",
            ownerUserId: "alice",
            accessLevel: DocumentAccessLevel.Private,
            now);
        var indexingJob = CreatePendingJob(document.Id, maxAttempts: 3, now);

        await secondScope.Metadata.CreateDocumentWithJobAsync(
            document,
            indexingJob,
            TestContext.Current.CancellationToken);

        var persistedDocument = await secondScope.Metadata.GetDocumentForIndexingAsync(
            document.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(persistedDocument);
        Assert.Equal(document.Id, persistedDocument.Id);
    }

    [DockerAvailableFact]
    public async Task IndexingPreflight_RejectsOldSchemaBeforeConsumingJobAttempt()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");

        try
        {
            await ResetGenAiSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
                connectionString,
                "001-enable-pgvector.sql",
                "002-document-ingestion.sql");

            using var scope = await CreateRepositoryScopeAsync(connectionString);
            var document = CreateDocument(
                tenantId: "tenant-a",
                ownerUserId: "alice",
                accessLevel: DocumentAccessLevel.Private,
                now);
            var indexingJob = CreatePendingJob(document.Id, maxAttempts: 1, now);

            await scope.Metadata.CreateDocumentWithJobAsync(
                document,
                indexingJob,
                TestContext.Current.CancellationToken);

            var expiredException = await Assert.ThrowsAsync<DocumentIndexingSchemaNotReadyException>(() =>
                scope.Jobs.MarkExpiredIndexingJobsFailedAsync(
                    TimeSpan.FromHours(1),
                    TestContext.Current.CancellationToken));
            var claimException = await Assert.ThrowsAsync<DocumentIndexingSchemaNotReadyException>(() =>
                scope.Jobs.ClaimNextPendingJobAsync(
                    "worker-old-schema",
                    TimeSpan.FromHours(1),
                    TestContext.Current.CancellationToken));
            var ownership = await ReadJobOwnershipAsync(connectionString, indexingJob.Id);
            var status = await scope.Metadata.GetDocumentStatusAsync(
                document.Id,
                "tenant-a",
                "alice",
                TestContext.Current.CancellationToken);

            Assert.Contains("003-pgvector-retrieval.sql", expiredException.Message);
            Assert.Contains("003-pgvector-retrieval.sql", claimException.Message);
            Assert.Equal(IndexingJobStatus.Pending.ToString(), ownership.Status);
            Assert.Equal(0, ownership.Attempts);
            Assert.Null(ownership.WorkerId);
            Assert.Null(ownership.StartedAtUtc);
            Assert.Null(ownership.CompletedAtUtc);
            Assert.NotNull(status);
            Assert.Equal(DocumentIndexingStatus.PendingIndexing, status.Document.IndexingStatus);
            Assert.Equal(IndexingJobStatus.Pending, status.LatestJob?.Status);

            await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
                connectionString,
                "003-pgvector-retrieval.sql");

            var claimed = await scope.Jobs.ClaimNextPendingJobAsync(
                "worker-after-migration",
                TimeSpan.FromHours(1),
                TestContext.Current.CancellationToken);

            Assert.NotNull(claimed);
            Assert.Equal(1, claimed.Attempts);
            Assert.Equal("worker-after-migration", claimed.WorkerId);
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Schema_RejectsInvalidDocumentAccessLevel()
    {
        using var scope = await CreateRepositoryScopeAsync();
        await CleanDatabaseAsync(scope);

        await using var connection = new NpgsqlConnection(scope.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.documents (
                id, tenant_id, owner_user_id, file_name, title, content_type, source_extension,
                storage_path, size_bytes, content_hash, version, access_level, indexing_status,
                created_at_utc, updated_at_utc, failure_reason)
            VALUES (
                @id, 'tenant-a', 'alice', 'notes.md', 'Notes', 'text/markdown', '.md',
                'memory://notes.md', 32, @content_hash, 1, 'TeamOnly', 'PendingIndexing',
                @now, @now, NULL);
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("content_hash", new string('a', 64));
        command.Parameters.AddWithValue("now", DateTimeOffset.Parse("2026-05-09T12:00:00Z"));

        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    private async Task<RepositoryScope> CreateRepositoryScopeAsync(string? connectionString = null)
    {
        connectionString ??= await postgres.GetConnectionStringAsync();
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
        return new RepositoryScope(
            serviceProvider,
            connectionString,
            serviceProvider.GetRequiredService<IDocumentMetadataRepository>(),
            serviceProvider.GetRequiredService<IIndexingJobRepository>());
    }

    private async Task<string> CreateAlternateDatabaseConnectionStringAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        var alternateDatabaseName = $"genai_platform_tests_{Guid.NewGuid():n}";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"CREATE DATABASE {QuoteIdentifier(alternateDatabaseName)};",
            connection);
        await command.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = alternateDatabaseName
        };
        return builder.ConnectionString;
    }

    private static string QuoteIdentifier(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static async Task CleanDatabaseAsync(RepositoryScope scope)
    {
        await EnsureSchemaAsync(scope.ConnectionString);
        await using var connection = new NpgsqlConnection(scope.ConnectionString);
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

    private static async Task EnsureSchemaAsync(string connectionString)
    {
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
    }

    private static async Task<JobOwnership> ReadJobOwnershipAsync(
        string connectionString,
        Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT status, worker_id, started_at_utc, completed_at_utc, attempts
            FROM genai.indexing_jobs
            WHERE id = @id;
            """, connection);
        command.Parameters.AddWithValue("id", jobId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Indexing job was not found.");
        }

        return new JobOwnership(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : new DateTimeOffset(reader.GetDateTime(2)),
            reader.IsDBNull(3) ? null : new DateTimeOffset(reader.GetDateTime(3)),
            reader.GetInt32(4));
    }

    private static Document CreateDocument(
        string tenantId,
        string ownerUserId,
        DocumentAccessLevel accessLevel,
        DateTimeOffset now)
    {
        return new Document(
            Guid.NewGuid(),
            tenantId,
            ownerUserId,
            "notes.md",
            "Notes",
            "text/markdown",
            ".md",
            $"memory://{Guid.NewGuid():n}/notes.md",
            SizeBytes: 32,
            new string('a', 64),
            Version: 1,
            accessLevel,
            DocumentIndexingStatus.PendingIndexing,
            now,
            now,
            FailureReason: null);
    }

    private static IndexingJob CreatePendingJob(
        Guid documentId,
        int maxAttempts,
        DateTimeOffset now)
    {
        return new IndexingJob(
            Guid.NewGuid(),
            documentId,
            IndexingJobStatus.Pending,
            Attempts: 0,
            maxAttempts,
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
        int position,
        string text,
        DateTimeOffset now)
    {
        return new DocumentChunk(
            Guid.NewGuid(),
            document.Id,
            document.Version,
            position,
            text,
            new string((char)('a' + position), 64),
            ApproximateTokenCount: 2,
            "test-profile",
            "v-test",
            [0.1f + position, 0.2f + position],
            "test-embedding",
            "test-provider",
            EmbeddingInputTokens: 2,
            now);
    }

    private sealed record RepositoryScope(
        ServiceProvider Services,
        string ConnectionString,
        IDocumentMetadataRepository Metadata, IIndexingJobRepository Jobs)
        : IDisposable
    {
        public void Dispose() => Services.Dispose();
    }

    private sealed record JobOwnership(
        string Status,
        string? WorkerId,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        int Attempts);
}
