using System.Text;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.UnitTests;

public sealed partial class DocumentIngestionTests
{
    [Fact]
    public async Task UploadDocumentHandler_ValidatesFileAndCreatesPendingIndexingJob()
    {
        var repository = new CapturingDocumentRepository();
        var storage = new InMemoryDocumentStorage("# Notes");
        var handler = CreateUploadDocumentHandler(
            storage,
            repository,
            new FakeUserContext("alice", "tenant-a"),
            new DocumentIngestionOptions
            {
                MaxUploadBytes = 1024,
                MaxIndexingAttempts = 4
            },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Notes"));

        var response = await handler.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
            new UploadDocumentCommand(
                "notes.md",
                "text/markdown",
                stream.Length,
                "Team Notes",
                "TenantPublic",
                stream),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, response.DocumentId);
        Assert.Equal("PendingIndexing", response.IndexingStatus);
        Assert.Equal("Pending", response.IndexingJobStatus);

        Assert.NotNull(repository.CreatedDocument);
        Assert.Equal("tenant-a", repository.CreatedDocument.TenantId);
        Assert.Equal("alice", repository.CreatedDocument.OwnerUserId);
        Assert.Equal(".md", repository.CreatedDocument.SourceExtension);
        Assert.Equal(DocumentAccessLevel.TenantPublic, repository.CreatedDocument.AccessLevel);
        Assert.Equal(DocumentIndexingStatus.PendingIndexing, repository.CreatedDocument.IndexingStatus);

        Assert.NotNull(repository.CreatedIndexingJob);
        Assert.Equal(repository.CreatedDocument.Id, repository.CreatedIndexingJob.DocumentId);
        Assert.Equal(IndexingJobStatus.Pending, repository.CreatedIndexingJob.Status);
        Assert.Equal(4, repository.CreatedIndexingJob.MaxAttempts);
        Assert.Equal(1, storage.CommitCalls);
    }

    [Fact]
    public async Task UploadDocumentHandler_RejectsUnknownLengthContentWhenStoredContentExceedsLimit()
    {
        var repository = new CapturingDocumentRepository();
        var storage = new InMemoryDocumentStorage("unused");
        var handler = CreateUploadDocumentHandler(
            storage,
            repository,
            new FakeUserContext("alice", "tenant-a"),
            new DocumentIngestionOptions
            {
                MaxUploadBytes = 4
            },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Notes"));

        var exception = await Assert.ThrowsAsync<DocumentTooLargeException>(() =>
            handler.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
                new UploadDocumentCommand(
                    "notes.md",
                    "text/markdown",
                    Length: null,
                    "Team Notes",
                    "Private",
                    stream),
                CancellationToken.None));

        Assert.Equal("Document file must be 4 bytes or fewer.", exception.Message);
        Assert.Null(repository.CreatedDocument);
    }

    [Fact]
    public async Task UploadDocumentHandler_DeletesStagedFileWhenPostSaveValidationRejectsEmptyContent()
    {
        // Closes the partial-failure leak where post-save validation (SizeBytes <= 0
        // or too large) would throw AFTER documentStorage.SaveAsync had already
        // staged a file. Before the fix, the staged file leaked because the outer catch
        // only caught DocumentStorageLimitExceededException.
        var repository = new CapturingDocumentRepository();
        var storage = new InMemoryDocumentStorage("unused");
        var handler = CreateUploadDocumentHandler(
            storage,
            repository,
            new FakeUserContext("alice", "tenant-a"),
            new DocumentIngestionOptions
            {
                MaxUploadBytes = 1024
            },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        await using var emptyStream = new MemoryStream();

        // Length: null bypasses the pre-save validator (it skips empty-length checks for
        // unknown-length uploads). The empty stream then reaches SaveAsync, which stages
        // a 0-byte file. The post-save check (SizeBytes <= 0) must trigger rollback.
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            handler.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
                new UploadDocumentCommand(
                    "notes.md",
                    "text/markdown",
                    Length: null,
                    "Team Notes",
                    "Private",
                    emptyStream),
                CancellationToken.None));

        Assert.Equal(1, storage.SaveCalls);
        Assert.Equal(storage.SavedPath, Assert.Single(storage.DeletedPaths));
        Assert.Null(repository.CreatedDocument);
    }

    [Fact]
    public async Task UploadDocumentHandler_DeletesStoredDocumentWhenRepositoryProvesMetadataWasNotCommitted()
    {
        var repository = new CapturingDocumentRepository
        {
            ThrowOnCreate = true
        };
        var storage = new InMemoryDocumentStorage("# Notes");
        var handler = CreateUploadDocumentHandler(
            storage,
            repository,
            new FakeUserContext("alice", "tenant-a"),
            new DocumentIngestionOptions
            {
                MaxUploadBytes = 1024
            },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Notes"));

        await Assert.ThrowsAsync<DocumentMetadataNotCommittedException>(() =>
            handler.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
                new UploadDocumentCommand(
                    "notes.md",
                    "text/markdown",
                    stream.Length,
                    "Team Notes",
                    "Private",
                    stream),
                CancellationToken.None));

        Assert.Single(storage.DeletedPaths);
        Assert.Equal(storage.SavedPath, storage.DeletedPaths.Single());
        Assert.Equal(1, storage.CommitCalls);
    }

    [Fact]
    public async Task UploadDocumentHandler_RecordsRepositoryCreateNotStartedProofWhenRepositoryCreateDoesNotStart()
    {
        var repository = new CapturingDocumentRepository
        {
            ThrowBeforeRepositoryCreateStarts = true
        };
        var storage = new InMemoryDocumentStorage("# Notes", throwOnDelete: true);
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository();
        var logger = new CapturingLogger<DocumentUploadRollbackCoordinator>();
        var handler = CreateUploadDocumentHandler(
            storage,
            repository,
            cleanupRepository,
            new FakeUserContext("alice", "tenant-a"),
            logger,
            new DocumentIngestionOptions
            {
                MaxUploadBytes = 1024
            },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Notes"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
                new UploadDocumentCommand(
                    "notes.md",
                    "text/markdown",
                    stream.Length,
                    "Team Notes",
                    "Private",
                    stream),
                CancellationToken.None));

        Assert.Equal("Repository create did not start.", exception.Message);
        Assert.Single(storage.DeletedPaths);
        Assert.Equal(storage.SavedPath, storage.DeletedPaths.Single());
        var cleanupRequest = Assert.Single(cleanupRepository.CleanupRequests);
        Assert.Equal(DocumentStorageCleanupProof.RepositoryCreateNotStarted, cleanupRequest.MetadataAbsenceProof);
        Assert.Equal(1, storage.CommitCalls);
        Assert.Null(repository.CreatedDocument);
        Assert.Null(repository.CreatedIndexingJob);
    }

    [Fact]
    public async Task UploadDocumentHandler_PreservesCommittedStorageWhenRepositoryCreateOutcomeIsUnknownEvenIfImmediateLookupWouldMissMetadata()
    {
        var repository = new CapturingDocumentRepository
        {
            ThrowAfterCreate = true,
            HideMetadataOnFirstExistsCheck = true
        };
        var storage = new InMemoryDocumentStorage("# Notes");
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository();
        var logger = new CapturingLogger<DocumentUploadRollbackCoordinator>();
        var handler = CreateUploadDocumentHandler(
            storage,
            repository,
            cleanupRepository,
            new FakeUserContext("alice", "tenant-a"),
            logger,
            new DocumentIngestionOptions
            {
                MaxUploadBytes = 1024
            },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Notes"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
                new UploadDocumentCommand(
                    "notes.md",
                    "text/markdown",
                    stream.Length,
                    "Team Notes",
                    "Private",
                    stream),
                CancellationToken.None));

        Assert.Empty(storage.DeletedPaths);
        Assert.Empty(cleanupRepository.CleanupRequests);
        Assert.NotNull(repository.CreatedDocument);
        Assert.NotNull(repository.CreatedIndexingJob);
        Assert.Equal(0, repository.DocumentExistsCalls);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning &&
                     entry.Message.Contains("Preserved stored document", StringComparison.Ordinal));
        Assert.False(await repository.DocumentExistsAsync(repository.CreatedDocument.Id, CancellationToken.None));
        Assert.True(await repository.DocumentExistsAsync(repository.CreatedDocument.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UploadDocumentHandler_DoesNotCreateMetadataWhenStorageCommitFails()
    {
        var repository = new CapturingDocumentRepository();
        var storage = new InMemoryDocumentStorage("# Notes", throwOnCommit: true);
        var handler = CreateUploadDocumentHandler(
            storage,
            repository,
            new FakeUserContext("alice", "tenant-a"),
            new DocumentIngestionOptions
            {
                MaxUploadBytes = 1024
            },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Notes"));

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            handler.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
                new UploadDocumentCommand(
                    "notes.md",
                    "text/markdown",
                    stream.Length,
                    "Team Notes",
                    "Private",
                    stream),
                CancellationToken.None));

        Assert.Equal("Commit failed.", exception.Message);
        Assert.Equal(1, storage.CommitCalls);
        Assert.Single(storage.DeletedPaths);
        Assert.Equal(storage.SavedPath, storage.DeletedPaths.Single());
        Assert.Null(repository.CreatedDocument);
        Assert.Null(repository.CreatedIndexingJob);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("Public")]
    public async Task UploadDocumentHandler_RejectsAmbiguousAccessLevelBeforeSaving(string accessLevel)
    {
        var repository = new CapturingDocumentRepository();
        var storage = new InMemoryDocumentStorage("# Notes");
        var handler = CreateUploadDocumentHandler(
            storage,
            repository,
            new FakeUserContext("alice", "tenant-a"),
            new DocumentIngestionOptions
            {
                MaxUploadBytes = 1024
            },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Notes"));

        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            handler.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
                new UploadDocumentCommand(
                    "notes.md",
                    "text/markdown",
                    stream.Length,
                    "Team Notes",
                    accessLevel,
                    stream),
                CancellationToken.None));

        Assert.Equal(
            "Document access level must be 'Private' or 'TenantPublic'.",
            Assert.Single(exception.Failures).ErrorMessage);
        Assert.Equal(0, storage.SaveCalls);
        Assert.Null(repository.CreatedDocument);
    }

    [Fact]
    public async Task UploadDocumentHandler_RecordsOrphanedCleanupWhenRollbackDeleteFails()
    {
        var repository = new CapturingDocumentRepository
        {
            ThrowOnCreate = true
        };
        var storage = new InMemoryDocumentStorage("# Notes", throwOnDelete: true);
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository();
        var logger = new CapturingLogger<DocumentUploadRollbackCoordinator>();
        var handler = CreateUploadDocumentHandler(
            storage,
            repository,
            cleanupRepository,
            new FakeUserContext("alice", "tenant-a"),
            logger,
            new DocumentIngestionOptions
            {
                MaxUploadBytes = 1024
            },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Notes"));

        var exception = await Assert.ThrowsAsync<DocumentMetadataNotCommittedException>(() =>
            handler.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
                new UploadDocumentCommand(
                    "notes.md",
                    "text/markdown",
                    stream.Length,
                    "Team Notes",
                    "Private",
                    stream),
                CancellationToken.None));

        Assert.Equal("Repository create failed before metadata was committed.", exception.Message);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Single(storage.DeletedPaths);
        var cleanupRequest = Assert.Single(cleanupRepository.CleanupRequests);
        Assert.NotEqual(Guid.Empty, cleanupRequest.DocumentId);
        Assert.Equal(storage.SavedPath, cleanupRequest.StoragePath);
        Assert.Null(cleanupRequest.StagedStoragePath);
        Assert.Equal(new string('b', 64), cleanupRequest.ContentHash);
        Assert.Equal(stream.Length, cleanupRequest.SizeBytes);
        Assert.Equal(
            nameof(DocumentMetadataNotCommittedException),
            cleanupRequest.MetadataAbsenceProof);
        Assert.Equal("IOException", cleanupRequest.DeleteFailureReason);
        Assert.Equal(
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            cleanupRequest.MetadataAbsenceVerifiedAtUtc);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning &&
                      entry.Message.Contains("Failed to delete stored document", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UploadDocumentHandler_PreservesPrimaryFailureWhenRollbackDeleteAndCleanupRecordFail()
    {
        // Codifies the contract that the primary upload failure (here: repository.Create throwing
        // DocumentMetadataNotCommittedException) is the user-facing root cause and reaches the API
        // layer for accurate status mapping even when the rollback path also fails. The rollback
        // failure must remain observable through structured logging, but must not displace the
        // primary exception on the throw chain.
        var repository = new CapturingDocumentRepository
        {
            ThrowOnCreate = true
        };
        var storage = new InMemoryDocumentStorage(
            "# Notes",
            throwOnDelete: true);
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository(throwOnRecord: true);
        var logger = new CapturingLogger<DocumentUploadRollbackCoordinator>();
        var handler = CreateUploadDocumentHandler(
            storage,
            repository,
            cleanupRepository,
            new FakeUserContext("alice", "tenant-a"),
            logger,
            new DocumentIngestionOptions
            {
                MaxUploadBytes = 1024
            },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Notes"));

        await Assert.ThrowsAsync<DocumentMetadataNotCommittedException>(() =>
            handler.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
                new UploadDocumentCommand(
                    "notes.md",
                    "text/markdown",
                    stream.Length,
                    "Team Notes",
                    "Private",
                    stream),
                CancellationToken.None));

        Assert.Single(storage.DeletedPaths);
        Assert.Empty(cleanupRepository.CleanupRequests);
        // The rollback coordinator still records the orphan-cleanup-recording failure through its
        // own logger; the workflow logger captures the higher-level "rollback failed after primary
        // failure" event separately. Asserting the coordinator-level log here is sufficient to
        // prove the rollback failure remains observable.
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error &&
                     entry.Message.Contains("Failed to record orphaned document storage cleanup", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData((int)DocumentUploadRollbackState.StorageNotCommitted, DocumentStorageCleanupProof.StorageNotCommitted)]
    [InlineData((int)DocumentUploadRollbackState.RepositoryCreateNotStarted, DocumentStorageCleanupProof.RepositoryCreateNotStarted)]
    [InlineData((int)DocumentUploadRollbackState.MetadataNotCommitted, DocumentStorageCleanupProof.MetadataNotCommitted)]
    public async Task DocumentUploadRollbackCoordinator_RecordsCleanupProofForDeletableStates(
        int stateValue,
        string expectedProof)
    {
        var state = (DocumentUploadRollbackState)stateValue;
        var storage = new InMemoryDocumentStorage("# Notes", throwOnDelete: true);
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository();
        var logger = new CapturingLogger<DocumentUploadRollbackCoordinator>();
        var coordinator = new DocumentUploadRollbackCoordinator(
            storage,
            cleanupRepository,
            logger,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        var storedDocument = new StoredDocument(
            "memory://rollback-proof.md",
            new string('c', 64),
            SizeBytes: 42);

        await coordinator.HandleFailureAsync(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            storedDocument,
            state);

        Assert.Equal("memory://rollback-proof.md", Assert.Single(storage.DeletedPaths));
        var cleanupRequest = Assert.Single(cleanupRepository.CleanupRequests);
        Assert.Equal(expectedProof, cleanupRequest.MetadataAbsenceProof);
    }

    [Fact]
    public void DocumentUploadRollbackCoordinator_MapsEveryRollbackStateToExpectedCleanupProof()
    {
        var expected = new Dictionary<DocumentUploadRollbackState, string?>
        {
            [DocumentUploadRollbackState.StorageNotCommitted] = DocumentStorageCleanupProof.StorageNotCommitted,
            [DocumentUploadRollbackState.RepositoryCreateNotStarted] = DocumentStorageCleanupProof.RepositoryCreateNotStarted,
            [DocumentUploadRollbackState.MetadataNotCommitted] = DocumentStorageCleanupProof.MetadataNotCommitted,
            [DocumentUploadRollbackState.MetadataOutcomeUnknown] = null
        };
        var enumValues = Enum
            .GetValues<DocumentUploadRollbackState>()
            .OrderBy(static state => (int)state)
            .ToArray();

        Assert.Equal(
            expected.Keys.OrderBy(static state => (int)state).ToArray(),
            enumValues);
        foreach (var state in enumValues)
        {
            var proof = DocumentUploadRollbackCoordinator.GetMetadataAbsenceProof(state);

            Assert.Equal(expected[state], proof);
            if (proof is not null)
            {
                Assert.True(DocumentStorageCleanupProof.IsValid(proof));
            }
        }
    }

    [Fact]
    public async Task DocumentUploadRollbackCoordinator_PreservesStorageWhenMetadataOutcomeIsUnknown()
    {
        var storage = new InMemoryDocumentStorage("# Notes", throwOnDelete: true);
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository();
        var logger = new CapturingLogger<DocumentUploadRollbackCoordinator>();
        var coordinator = new DocumentUploadRollbackCoordinator(
            storage,
            cleanupRepository,
            logger,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));
        var storedDocument = new StoredDocument(
            "memory://unknown-metadata.md",
            new string('c', 64),
            SizeBytes: 42);

        await coordinator.HandleFailureAsync(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            storedDocument,
            DocumentUploadRollbackState.MetadataOutcomeUnknown);

        Assert.Empty(storage.DeletedPaths);
        Assert.Empty(cleanupRepository.CleanupRequests);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning &&
                     entry.Message.Contains("Preserved stored document", StringComparison.Ordinal));
    }

}
