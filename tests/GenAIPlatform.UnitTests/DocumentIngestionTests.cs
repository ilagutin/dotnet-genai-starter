using System.Text;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Embedding;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Failure;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Lease;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Observability.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed class DocumentIngestionTests
{
    [Fact]
    public void TextChunker_CreatesStableChunksWithVersionMetadata()
    {
        var document = CreateDocument();
        var chunker = new TextChunker(Options.Create(new DocumentIngestionOptions
        {
            ChunkMaxCharacters = 80,
            ChunkOverlapCharacters = 12,
            ChunkingProfile = "test-profile",
            ChunkingProfileVersion = "v-test"
        }));
        var text = string.Join(' ', Enumerable.Range(1, 60).Select(static index => $"word{index}"));
        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");

        var chunks = chunker.Chunk(document, text, now);
        var repeatedChunks = chunker.Chunk(document, text, now);

        Assert.True(chunks.Count > 1);
        Assert.Equal(
            chunks.Select(static chunk => chunk.Id),
            repeatedChunks.Select(static chunk => chunk.Id));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(static chunk => chunk.Position));
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(document.Id, chunk.DocumentId);
            Assert.Equal(document.Version, chunk.DocumentVersion);
            Assert.Equal("test-profile", chunk.ChunkingProfile);
            Assert.Equal("v-test", chunk.ChunkingProfileVersion);
            Assert.Matches("^[a-f0-9]{64}$", chunk.TextHash);
            Assert.True(chunk.ApproximateTokenCount > 0);
        });
    }

    [Fact]
    public void TextChunker_RespectsSmallConfiguredChunkSize()
    {
        var document = CreateDocument();
        var chunker = new TextChunker(Options.Create(new DocumentIngestionOptions
        {
            ChunkMaxCharacters = 40,
            ChunkOverlapCharacters = 0
        }));
        var text = new string('a', 95);

        var chunks = chunker.Chunk(
            document,
            text,
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"));

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= 40));
    }

    [Fact]
    public async Task PlainTextDocumentTextExtractor_RejectsInvalidUtf8()
    {
        var extractor = new PlainTextDocumentTextExtractor();
        await using var stream = new MemoryStream([0xC3, 0x28]);

        var exception = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            extractor.ExtractAsync(
                CreateDocument(),
                stream,
                CancellationToken.None));

        Assert.Equal("Document text must be valid UTF-8.", exception.Message);
    }

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

    [Fact]
    public async Task ProcessIndexingJobsHandler_ExtractsChunksEmbedsAndCompletesJob()
    {
        var document = CreateDocument();
        var job = new IndexingJob(
            Guid.NewGuid(),
            document.Id,
            IndexingJobStatus.Processing,
            Attempts: 1,
            MaxAttempts: 3,
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            CompletedAtUtc: null,
            WorkerId: "worker-1",
            FailureReason: null);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job
        };
        var requestLogRepository = new CapturingAiRequestLogRepository();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient: new FakeEmbeddingClient(),
            storage: new InMemoryDocumentStorage("First paragraph with enough text for chunking.\n\nSecond paragraph."),
            textChunker: new TextChunker(Options.Create(new DocumentIngestionOptions
            {
                ChunkMaxCharacters = 48,
                ChunkOverlapCharacters = 8
            })),
            options: new DocumentIngestionOptions
            {
                MaxIndexingJobsPerPoll = 2,
                MaxIndexingAttempts = 3
            },
            embeddingOptions: new EmbeddingOptions
            {
                DefaultModel = "test-embedding"
            },
            aiRequestLogRepository: requestLogRepository);

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 2, CorrelationId: "test-correlation"),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(1, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.NotEmpty(repository.CompletedChunks);
        Assert.All(repository.CompletedChunks, chunk =>
        {
            Assert.Equal("test-embedding", chunk.EmbeddingModel);
            Assert.Equal("fake", chunk.EmbeddingProvider);
            Assert.Equal(3, chunk.EmbeddingDimensions);
            Assert.NotEmpty(chunk.Text);
        });
        Assert.Equal(job.Id, repository.CompletedIndexingJob?.Id);
        Assert.Equal(TimeSpan.FromMinutes(15), repository.LastProcessingLeaseDuration);
        Assert.True(repository.RenewProcessingLeaseCalls > 0);
        Assert.Equal(job.Id, repository.LastRenewedIndexingJob?.Id);
        Assert.Empty(requestLogRepository.Entries);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_DoesNotCountStaleCompletionAsIndexed()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job,
            CompleteIndexingResult = false
        };
        var handler = CreateProcessIndexingJobsHandler(repository, new FakeEmbeddingClient());

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(0, response.Retried);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_DoesNotRecordFailureWhenCompletionCommitOutcomeIsUnknownButCompleted()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job,
            ThrowCompletionUnknownAfterCommit = true
        };
        var logger = new CapturingLogger<ProcessIndexingJobsHandler>();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            new FakeEmbeddingClient(),
            logger: logger);

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(0, response.Retried);
        Assert.Equal(job.Id, repository.CompletedIndexingJob?.Id);
        Assert.NotEmpty(repository.CompletedChunks);
        Assert.Null(repository.LastFailedIndexingJob);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning &&
                     entry.Message.Contains("durable completion outcome is unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_ReleasesProcessingJobWhenIndexingSchemaFailsBeforeSideEffects()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 1);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job,
            ThrowSchemaNotReadyOnGetDocumentForIndexing = true
        };
        var embeddingClient = new FakeEmbeddingClient();
        var handler = CreateProcessIndexingJobsHandler(repository, embeddingClient);

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(0, response.Retried);
        Assert.Equal(0, embeddingClient.Calls);
        Assert.Null(repository.CompletedIndexingJob);
        Assert.Empty(repository.CompletedChunks);
        Assert.Equal(job.Id, repository.ReleasedIndexingJob?.Id);
        Assert.Null(repository.LastFailedIndexingJob);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_RecordsSchemaFailureAfterSideEffectsWithoutReleasingAttempt()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job,
            ThrowSchemaNotReadyOnComplete = true
        };
        var embeddingClient = new FakeEmbeddingClient();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient,
            options: new DocumentIngestionOptions
            {
                MaxIndexingJobsPerPoll = 1,
                IndexingRetryDelaySeconds = 30
            });

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(1, response.Retried);
        Assert.Equal(job.Id, repository.CompletedIndexingJob?.Id);
        Assert.NotEmpty(repository.CompletedChunks);
        Assert.Equal(repository.CompletedChunks.Count, embeddingClient.Calls);
        Assert.Null(repository.ReleasedIndexingJob);
        Assert.Equal(job.Id, repository.LastFailedIndexingJob?.Id);
        Assert.True(repository.LastFailureRetry);
        Assert.Equal(TimeSpan.FromSeconds(30), repository.LastFailureRetryDelay);
        Assert.Equal("Document indexing schema is not ready.", repository.LastFailureReason);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_UsesPersistedMaxAttemptsForRetryDecision()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 2, maxAttempts: 2);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job
        };
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            new ThrowingEmbeddingClient(),
            options: new DocumentIngestionOptions
            {
                MaxIndexingAttempts = 10,
                MaxIndexingJobsPerPoll = 1
            });

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Retried);
        Assert.Equal(1, response.Failed);
        Assert.False(repository.LastFailureRetry);
        Assert.Equal(job.Id, repository.LastFailedIndexingJob?.Id);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_DoesNotRetryPermanentDocumentValidationFailure()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job
        };
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient: new FakeEmbeddingClient(),
            storage: new InMemoryDocumentStorage("   \r\n   "),
            textChunker: new TextChunker(Options.Create(new DocumentIngestionOptions())),
            options: new DocumentIngestionOptions
            {
                MaxIndexingJobsPerPoll = 1,
                MaxIndexingAttempts = 3,
                IndexingRetryDelaySeconds = 30
            },
            embeddingOptions: new EmbeddingOptions
            {
                DefaultModel = "test-embedding"
            });

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Retried);
        Assert.Equal(1, response.Failed);
        Assert.False(repository.LastFailureRetry);
        Assert.Equal(job.Id, repository.LastFailedIndexingJob?.Id);
        Assert.Equal("Document text is empty.", repository.LastFailureReason);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_StoresSafeFailureReasonForProviderErrors()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 1);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job
        };
        var logger = new CapturingLogger<ProcessIndexingJobsHandler>();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            new ThrowingEmbeddingClient("provider leaked C:\\secrets\\api-key.txt"),
            logger: logger);

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Failed);
        Assert.Equal(
            "Embedding provider failed while indexing the document.",
            repository.LastFailureReason);
        Assert.DoesNotContain("secrets", repository.LastFailureReason);
        Assert.DoesNotContain("provider leaked", repository.LastFailureReason);
        var logEntry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, logEntry.Level);
        Assert.Contains("Document indexing failed", logEntry.Message);
        Assert.DoesNotContain("secrets", logEntry.Message);
        Assert.DoesNotContain("provider leaked", logEntry.Message);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_RejectsZeroEmbeddingAsPermanentProviderFailure()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job
        };
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            new FixedEmbeddingClient([0f, 0f]));

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(1, response.Failed);
        Assert.Equal(0, response.Retried);
        Assert.Empty(repository.CompletedChunks);
        Assert.False(repository.LastFailureRetry);
        Assert.Equal(job.Id, repository.LastFailedIndexingJob?.Id);
        Assert.Equal(
            "Embedding provider returned an invalid embedding vector.",
            repository.LastFailureReason);
    }

    [Theory]
    [InlineData("null-vector")]
    [InlineData("blank-model")]
    [InlineData("blank-provider")]
    [InlineData("negative-tokens")]
    public async Task ProcessIndexingJobsHandler_RejectsMalformedEmbeddingMetadataAsPermanentProviderFailure(
        string responseShape)
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job
        };
        var embeddingClient = responseShape switch
        {
            "null-vector" => new FixedEmbeddingClient(null),
            "blank-model" => new FixedEmbeddingClient([0.1f, 0.2f], model: " "),
            "blank-provider" => new FixedEmbeddingClient([0.1f, 0.2f], provider: " "),
            "negative-tokens" => new FixedEmbeddingClient([0.1f, 0.2f], inputTokens: -1),
            _ => throw new InvalidOperationException($"Unknown response shape '{responseShape}'.")
        };
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient);

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(1, response.Failed);
        Assert.Equal(0, response.Retried);
        Assert.Empty(repository.CompletedChunks);
        Assert.False(repository.LastFailureRetry);
        Assert.Equal(job.Id, repository.LastFailedIndexingJob?.Id);
        Assert.Equal(
            "Embedding provider returned an invalid embedding vector.",
            repository.LastFailureReason);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_FailsWhenChunkExceedsEmbeddingInputLimit()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var oversizedText = "oversized chunk text that must not be logged";
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job
        };
        var embeddingClient = new FakeEmbeddingClient();
        var logger = new CapturingLogger<ProcessIndexingJobsHandler>();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient: embeddingClient,
            storage: new InMemoryDocumentStorage("source text"),
            textChunker: new FixedTextChunker([CreateChunkShell(document, position: 0, oversizedText)]),
            options: new DocumentIngestionOptions
            {
                MaxIndexingJobsPerPoll = 1,
                MaxIndexingAttempts = 3
            },
            embeddingOptions: new EmbeddingOptions
            {
                DefaultModel = "test-embedding",
                MaxInputCharacters = 10
            },
            logger: logger);

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(1, response.Failed);
        Assert.Equal(0, response.Retried);
        Assert.Equal(0, embeddingClient.Calls);
        Assert.Empty(repository.CompletedChunks);
        Assert.False(repository.LastFailureRetry);
        Assert.Equal(job.Id, repository.LastFailedIndexingJob?.Id);
        Assert.Equal(
            "Document chunk text exceeds the configured embedding input limit.",
            repository.LastFailureReason);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains(oversizedText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_StopsWhenProcessingLeaseCannotBeRenewed()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job,
            RenewProcessingLeaseResult = false
        };
        var logger = new CapturingLogger<ProcessIndexingJobsHandler>();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            new FakeEmbeddingClient(),
            logger: logger);

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(0, response.Retried);
        Assert.Equal(1, repository.RenewProcessingLeaseCalls);
        Assert.Null(repository.CompletedIndexingJob);
        Assert.Null(repository.LastFailedIndexingJob);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Information &&
                     entry.Message.Contains("Skipped stale indexing job", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_RenewsLeaseWhileEmbeddingCallIsInFlight()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job
        };
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient: new SlowEmbeddingClient(TimeSpan.FromMilliseconds(1100)),
            storage: new InMemoryDocumentStorage("A short document that produces one chunk."),
            textChunker: new TextChunker(Options.Create(new DocumentIngestionOptions
            {
                ChunkMaxCharacters = 500,
                ChunkOverlapCharacters = 0
            })),
            options: new DocumentIngestionOptions
            {
                MaxIndexingJobsPerPoll = 1,
                ProcessingJobLeaseSeconds = 3
            },
            embeddingOptions: new EmbeddingOptions
            {
                DefaultModel = "test-embedding"
            });

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Indexed);
        Assert.True(repository.RenewProcessingLeaseCalls >= 5);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_PassesProcessingLeaseDurationWhenClaimingAndCleanup()
    {
        var repository = new CapturingDocumentRepository();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient: new FakeEmbeddingClient(),
            storage: new InMemoryDocumentStorage("No job should be opened."),
            textChunker: new TextChunker(Options.Create(new DocumentIngestionOptions())),
            options: new DocumentIngestionOptions
            {
                MaxIndexingJobsPerPoll = 1,
                ProcessingJobLeaseSeconds = 60
            },
            embeddingOptions: new EmbeddingOptions());

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(0, response.Claimed);
        Assert.Equal(TimeSpan.FromSeconds(60), repository.LastProcessingLeaseDuration);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_ReturnsExpiredOrExhaustedCleanupCount()
    {
        var repository = new CapturingDocumentRepository
        {
            ExpiredOrExhaustedFailures = 2
        };
        var handler = CreateProcessIndexingJobsHandler(repository, new FakeEmbeddingClient());

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(0, response.Claimed);
        Assert.Equal(2, response.ExpiredOrExhaustedFailed);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_ReleasesProcessingJobWhenCanceledBeforeSideEffects()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job,
            CancelGetDocumentForIndexing = true
        };
        var handler = CreateProcessIndexingJobsHandler(repository, new FakeEmbeddingClient());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(
                new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
                cancellation.Token));

        Assert.Equal(job.Id, repository.ReleasedIndexingJob?.Id);
        Assert.Null(repository.LastFailedIndexingJob);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_ConsumesAttemptWhenCanceledAfterStorageSideEffectsStart()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job
        };
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient: new FakeEmbeddingClient(),
            storage: new CancelingDocumentStorage(),
            textChunker: new TextChunker(Options.Create(new DocumentIngestionOptions())),
            options: new DocumentIngestionOptions
            {
                MaxIndexingJobsPerPoll = 1,
                MaxIndexingAttempts = 3,
                IndexingRetryDelaySeconds = 30
            },
            embeddingOptions: new EmbeddingOptions
            {
                DefaultModel = "test-embedding"
            });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(
                new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
                cancellation.Token));

        Assert.Null(repository.ReleasedIndexingJob);
        Assert.Equal(job.Id, repository.LastFailedIndexingJob?.Id);
        Assert.True(repository.LastFailureRetry);
        Assert.Equal(
            "Indexing job was interrupted while processing the document.",
            repository.LastFailureReason);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_CancelsEmbeddingCallWhenLeaseBecomesStaleInFlight()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job,
            FailRenewProcessingLeaseAfterCalls = 3
        };
        var embeddingClient = new CancellableSlowEmbeddingClient();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient: embeddingClient,
            storage: new InMemoryDocumentStorage("A short document that produces one chunk."),
            textChunker: new TextChunker(Options.Create(new DocumentIngestionOptions
            {
                ChunkMaxCharacters = 500,
                ChunkOverlapCharacters = 0
            })),
            options: new DocumentIngestionOptions
            {
                MaxIndexingJobsPerPoll = 1,
                ProcessingJobLeaseSeconds = 3
            },
            embeddingOptions: new EmbeddingOptions
            {
                DefaultModel = "test-embedding"
            });

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(0, response.Retried);
        Assert.True(embeddingClient.CancellationObserved);
        Assert.Null(repository.CompletedIndexingJob);
        Assert.Null(repository.LastFailedIndexingJob);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_LogsDiscardedEmbeddingResponseWhenLeaseBecomesStaleInFlight()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job,
            FailRenewProcessingLeaseAfterCalls = 3
        };
        var logger = new CapturingLogger<ProcessIndexingJobsHandler>();
        var requestLogRepository = new CapturingAiRequestLogRepository();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient: new CancellationIgnoringEmbeddingClient(
                TimeSpan.FromMilliseconds(1500),
                provider: "delayed-fake",
                inputTokens: 7),
            storage: new InMemoryDocumentStorage("A short document that produces one chunk."),
            textChunker: new TextChunker(Options.Create(new DocumentIngestionOptions
            {
                ChunkMaxCharacters = 500,
                ChunkOverlapCharacters = 0
            })),
            options: new DocumentIngestionOptions
            {
                MaxIndexingJobsPerPoll = 1,
                ProcessingJobLeaseSeconds = 3
            },
            embeddingOptions: new EmbeddingOptions
            {
                DefaultModel = "test-embedding"
            },
            logger: logger,
            aiRequestLogRepository: requestLogRepository);

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(0, response.Retried);
        Assert.Null(repository.CompletedIndexingJob);
        Assert.Null(repository.LastFailedIndexingJob);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning &&
                     entry.Message.Contains("Discarded embedding provider response", StringComparison.Ordinal) &&
                     entry.Message.Contains("delayed-fake", StringComparison.Ordinal) &&
                     entry.Message.Contains("test-embedding", StringComparison.Ordinal) &&
                     entry.Message.Contains("7", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains("A short document", StringComparison.Ordinal) ||
                     entry.Message.Contains("0.1", StringComparison.Ordinal));

        var usageEntry = Assert.Single(requestLogRepository.Entries);
        Assert.Equal("Succeeded", usageEntry.Status);
        Assert.Equal("indexing_abandoned", usageEntry.ErrorCode);
        Assert.Equal("delayed-fake", usageEntry.Provider);
        Assert.Equal("test-embedding", usageEntry.Model);
        Assert.Equal(7, usageEntry.EmbeddingTokens);
        Assert.Null(usageEntry.InputTokens);
        Assert.Null(usageEntry.OutputTokens);
        Assert.Null(usageEntry.TotalTokens);
        Assert.Null(usageEntry.Prompt);
        Assert.Empty(usageEntry.RetrievedDocuments);
        Assert.Equal(
            $"indexing-document-{document.Id:n}-job-{job.Id:n}",
            usageEntry.CorrelationId);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_DiscardedEmbeddingUsageLoggingFailurePreservesStaleLeaseOutcome()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job,
            FailRenewProcessingLeaseAfterCalls = 3
        };
        var logger = new CapturingLogger<ProcessIndexingJobsHandler>();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            embeddingClient: new CancellationIgnoringEmbeddingClient(
                TimeSpan.FromMilliseconds(1500),
                provider: "delayed-fake",
                inputTokens: 7),
            storage: new InMemoryDocumentStorage("A short document that produces one chunk."),
            textChunker: new TextChunker(Options.Create(new DocumentIngestionOptions
            {
                ChunkMaxCharacters = 500,
                ChunkOverlapCharacters = 0
            })),
            options: new DocumentIngestionOptions
            {
                MaxIndexingJobsPerPoll = 1,
                ProcessingJobLeaseSeconds = 3
            },
            embeddingOptions: new EmbeddingOptions
            {
                DefaultModel = "test-embedding"
            },
            logger: logger,
            aiRequestLogRepository: new ThrowingAiRequestLogRepository(),
            loggingFailureMode: AiRequestLoggingFailureMode.FailClosed);

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(0, response.Retried);
        Assert.Null(repository.CompletedIndexingJob);
        Assert.Null(repository.LastFailedIndexingJob);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error &&
                     entry.Message.Contains("Failed to log discarded embedding usage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_RecordsProviderCancellationAsRetryableFailure()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job
        };
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            new ProviderCancelingEmbeddingClient());

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            CancellationToken.None);

        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(1, response.Retried);
        Assert.Null(repository.ReleasedIndexingJob);
        Assert.Equal(job.Id, repository.LastFailedIndexingJob?.Id);
        Assert.True(repository.LastFailureRetry);
        Assert.Equal(
            "Document indexing dependency canceled before the worker stopped.",
            repository.LastFailureReason);
    }

    [Fact]
    public async Task ProcessIndexingJobsHandler_RecordsFailureWhenShutdownArrivesDuringFailureRecording()
    {
        var document = CreateDocument();
        var job = CreateProcessingJob(document.Id, attempts: 1, maxAttempts: 3);
        var repository = new CapturingDocumentRepository
        {
            DocumentForIndexing = document,
            PendingJob = job,
            ThrowIfFailureTokenCanceled = true
        };
        using var cancellation = new CancellationTokenSource();
        var handler = CreateProcessIndexingJobsHandler(
            repository,
            new CancelingThenThrowingEmbeddingClient(cancellation));

        var response = await handler.HandleAsync(
            new ProcessIndexingJobsCommand("worker-1", MaxJobs: 1),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, response.Claimed);
        Assert.Equal(0, response.Indexed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(1, response.Retried);
        Assert.Null(repository.ReleasedIndexingJob);
        Assert.Equal(job.Id, repository.LastFailedIndexingJob?.Id);
        Assert.True(repository.LastFailureRetry);
        Assert.Equal(
            "Embedding provider failed while indexing the document.",
            repository.LastFailureReason);
    }

    [Fact]
    public async Task GetDocumentStatusHandler_WithoutTenantFailsClosed()
    {
        var repository = new CapturingDocumentRepository();
        var handler = new GetDocumentStatusHandler(
            repository,
            new FakeUserContext("alice", tenantId: null));

        var response = await handler.HandleAsync(
            new GetDocumentStatusQuery(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Null(response);
        Assert.False(repository.StatusLookupCalled);
    }

    [Fact]
    public async Task GetDocumentStatusHandler_UnauthenticatedUserFailsClosed()
    {
        var repository = new CapturingDocumentRepository();
        var handler = new GetDocumentStatusHandler(
            repository,
            new FakeUserContext("alice", "tenant-a", isAuthenticated: false));

        var response = await handler.HandleAsync(
            new GetDocumentStatusQuery(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Null(response);
        Assert.False(repository.StatusLookupCalled);
    }

    [Fact]
    public async Task ProcessDocumentStorageCleanupHandler_DeletesRecordedOrphanWhenMetadataIsAbsent()
    {
        var repository = new CapturingDocumentRepository();
        var storage = new InMemoryDocumentStorage("unused");
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository();
        var cleanupRequest = CreateCleanupRequest();
        cleanupRepository.CleanupRequests.Add(cleanupRequest);
        var handler = CreateProcessDocumentStorageCleanupHandler(
            storage,
            cleanupRepository,
            repository,
            new CapturingLogger<DocumentStorageCleanupRequestProcessor>(),
            new DocumentIngestionOptions
            {
                MaxIndexingAttempts = 1,
                IndexingRetryDelaySeconds = 1,
                MaxStorageCleanupAttempts = 3,
                StorageCleanupRetryDelaySeconds = 45
            });

        var response = await handler.HandleAsync(
            new ProcessDocumentStorageCleanupCommand("worker-1", MaxRequests: 10),
            CancellationToken.None);

        Assert.Equal(1, response.Discovered);
        Assert.Equal(1, response.Deleted);
        Assert.Equal(0, response.Deferred);
        Assert.Equal(0, response.Failed);
        Assert.Contains(cleanupRequest.StoragePath, storage.DeletedPaths);
        Assert.Empty(cleanupRepository.CleanupRequests);
        Assert.Single(cleanupRepository.CompletedCleanupRequests);
        Assert.Equal(1, repository.DocumentExistsCalls);
    }

    [Fact]
    public async Task ProcessDocumentStorageCleanupHandler_DefersCleanupWhenMetadataExists()
    {
        var cleanupRequest = CreateCleanupRequest();
        var repository = new CapturingDocumentRepository();
        repository.ExistingDocumentIds.Add(cleanupRequest.DocumentId);
        var storage = new InMemoryDocumentStorage("unused");
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository();
        cleanupRepository.CleanupRequests.Add(cleanupRequest);
        var handler = CreateProcessDocumentStorageCleanupHandler(
            storage,
            cleanupRepository,
            repository,
            new CapturingLogger<DocumentStorageCleanupRequestProcessor>(),
            new DocumentIngestionOptions
            {
                MaxIndexingAttempts = 1,
                IndexingRetryDelaySeconds = 1,
                MaxStorageCleanupAttempts = 3,
                StorageCleanupRetryDelaySeconds = 45
            });

        var response = await handler.HandleAsync(
            new ProcessDocumentStorageCleanupCommand("worker-1", MaxRequests: 10),
            CancellationToken.None);

        Assert.Equal(1, response.Discovered);
        Assert.Equal(0, response.Deleted);
        Assert.Equal(1, response.Deferred);
        Assert.Equal(0, response.Failed);
        Assert.Empty(storage.DeletedPaths);
        var deferredRequest = Assert.Single(cleanupRepository.CleanupRequests);
        Assert.Equal(DocumentStorageCleanupStatus.Deferred, deferredRequest.Status);
        Assert.Empty(cleanupRepository.CompletedCleanupRequests);
    }

    [Fact]
    public async Task ProcessDocumentStorageCleanupHandler_DefersCleanupWhenMetadataVerificationFailsBeforeAttemptsExhausted()
    {
        var cleanupRequest = CreateCleanupRequest();
        var repository = new CapturingDocumentRepository
        {
            ThrowOnDocumentExists = true
        };
        var storage = new InMemoryDocumentStorage("unused");
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository();
        cleanupRepository.CleanupRequests.Add(cleanupRequest);
        var handler = CreateProcessDocumentStorageCleanupHandler(
            storage,
            cleanupRepository,
            repository,
            new CapturingLogger<DocumentStorageCleanupRequestProcessor>(),
            new DocumentIngestionOptions
            {
                MaxIndexingAttempts = 1,
                IndexingRetryDelaySeconds = 1,
                MaxStorageCleanupAttempts = 3,
                StorageCleanupRetryDelaySeconds = 45
            });

        var response = await handler.HandleAsync(
            new ProcessDocumentStorageCleanupCommand("worker-1", MaxRequests: 10),
            CancellationToken.None);

        Assert.Equal(1, response.Discovered);
        Assert.Equal(0, response.Deleted);
        Assert.Equal(1, response.Deferred);
        Assert.Equal(0, response.Failed);
        Assert.Empty(storage.DeletedPaths);
        var deferredRequest = Assert.Single(cleanupRepository.CleanupRequests);
        Assert.Equal(DocumentStorageCleanupStatus.Deferred, deferredRequest.Status);
        Assert.Equal("Failed to verify metadata absence.", deferredRequest.FailureReason);
        Assert.Equal(TimeSpan.FromSeconds(45), cleanupRepository.LastRetryDelay);
        Assert.Empty(cleanupRepository.CompletedCleanupRequests);
    }

    [Fact]
    public async Task ProcessDocumentStorageCleanupHandler_RetriesTransientDeleteFailureAndCompletesOnNextPoll()
    {
        var cleanupRequest = CreateCleanupRequest();
        var repository = new CapturingDocumentRepository();
        var storage = new InMemoryDocumentStorage(
            "unused",
            deleteFailuresBeforeSuccess: 1);
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository();
        cleanupRepository.CleanupRequests.Add(cleanupRequest);
        var handler = CreateProcessDocumentStorageCleanupHandler(
            storage,
            cleanupRepository,
            repository,
            new CapturingLogger<DocumentStorageCleanupRequestProcessor>(),
            new DocumentIngestionOptions());

        var firstResponse = await handler.HandleAsync(
            new ProcessDocumentStorageCleanupCommand("worker-1", MaxRequests: 10),
            CancellationToken.None);

        Assert.Equal(1, firstResponse.Discovered);
        Assert.Equal(0, firstResponse.Deleted);
        Assert.Equal(1, firstResponse.Deferred);
        Assert.Equal(0, firstResponse.Failed);
        var deferredRequest = Assert.Single(cleanupRepository.CleanupRequests);
        Assert.Equal(DocumentStorageCleanupStatus.Deferred, deferredRequest.Status);
        Assert.Equal("IOException", deferredRequest.FailureReason);
        Assert.Empty(cleanupRepository.CompletedCleanupRequests);

        var secondResponse = await handler.HandleAsync(
            new ProcessDocumentStorageCleanupCommand("worker-1", MaxRequests: 10),
            CancellationToken.None);

        Assert.Equal(1, secondResponse.Discovered);
        Assert.Equal(1, secondResponse.Deleted);
        Assert.Equal(0, secondResponse.Deferred);
        Assert.Equal(0, secondResponse.Failed);
        Assert.Contains(cleanupRequest.StoragePath, storage.DeletedPaths);
        Assert.Empty(cleanupRepository.CleanupRequests);
        Assert.Single(cleanupRepository.CompletedCleanupRequests);
    }

    [Fact]
    public async Task ProcessDocumentStorageCleanupHandler_FailsCleanupWhenDeleteFailsAfterAttemptsExhausted()
    {
        var cleanupRequest = CreateCleanupRequest() with
        {
            Attempts = 2
        };
        var repository = new CapturingDocumentRepository();
        var storage = new InMemoryDocumentStorage("unused", throwOnDelete: true);
        var cleanupRepository = new InMemoryDocumentStorageCleanupRepository();
        cleanupRepository.CleanupRequests.Add(cleanupRequest);
        var handler = CreateProcessDocumentStorageCleanupHandler(
            storage,
            cleanupRepository,
            repository,
            new CapturingLogger<DocumentStorageCleanupRequestProcessor>(),
            new DocumentIngestionOptions
            {
                MaxIndexingAttempts = 10,
                MaxStorageCleanupAttempts = 3
            });

        var response = await handler.HandleAsync(
            new ProcessDocumentStorageCleanupCommand("worker-1", MaxRequests: 10),
            CancellationToken.None);

        Assert.Equal(1, response.Discovered);
        Assert.Equal(0, response.Deleted);
        Assert.Equal(0, response.Deferred);
        Assert.Equal(1, response.Failed);
        var failedRequest = Assert.Single(cleanupRepository.CleanupRequests);
        Assert.Equal(DocumentStorageCleanupStatus.Failed, failedRequest.Status);
        Assert.Equal(3, failedRequest.Attempts);
        Assert.Equal("IOException", failedRequest.FailureReason);
        Assert.Empty(cleanupRepository.CompletedCleanupRequests);
    }

    private static IApplicationDispatcher CreateUploadDocumentHandler(
        IDocumentStorage storage,
        IDocumentIngestionRepository repository,
        IUserContext userContext,
        DocumentIngestionOptions options,
        TimeProvider timeProvider)
    {
        return CreateUploadDocumentHandler(
            storage,
            repository,
            new InMemoryDocumentStorageCleanupRepository(),
            userContext,
            new CapturingLogger<DocumentUploadRollbackCoordinator>(),
            options,
            timeProvider);
    }

    private static IApplicationDispatcher CreateUploadDocumentHandler(
        IDocumentStorage storage,
        IDocumentIngestionRepository repository,
        IDocumentStorageCleanupRepository cleanupRepository,
        IUserContext userContext,
        CapturingLogger<DocumentUploadRollbackCoordinator> logger,
        DocumentIngestionOptions options,
        TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(new Microsoft.Extensions.Configuration.ConfigurationManager());
        services.AddSingleton<IDocumentStorage>(storage);
        services.AddSingleton<IDocumentIngestionRepository>(repository);
        services.AddSingleton(cleanupRepository);
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton<ILogger<DocumentUploadRollbackCoordinator>>(logger);
        services.AddSingleton<ILogger<DocumentUploadRollbackInvoker>>(NullLogger<DocumentUploadRollbackInvoker>.Instance);
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(timeProvider);

        return services
            .BuildServiceProvider()
            .GetRequiredService<IApplicationDispatcher>();
    }

    private static Document CreateDocument()
    {
        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");
        return new Document(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "tenant-a",
            "alice",
            "notes.txt",
            "Notes",
            "text/plain",
            ".txt",
            "memory://notes.txt",
            100,
            new string('a', 64),
            Version: 1,
            DocumentAccessLevel.Private,
            DocumentIndexingStatus.PendingIndexing,
            now,
            now,
            FailureReason: null);
    }

    private static IndexingJob CreateProcessingJob(
        Guid documentId,
        int attempts,
        int maxAttempts)
    {
        return new IndexingJob(
            Guid.NewGuid(),
            documentId,
            IndexingJobStatus.Processing,
            attempts,
            maxAttempts,
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            CompletedAtUtc: null,
            WorkerId: "worker-1",
            FailureReason: null);
    }

    private static DocumentStorageCleanupRequest CreateCleanupRequest()
    {
        return new DocumentStorageCleanupRequest(
            Guid.NewGuid(),
            "memory://orphan.md",
            StagedStoragePath: null,
            new string('d', 64),
            SizeBytes: 32,
            nameof(DocumentMetadataNotCommittedException),
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"),
            "IOException");
    }

    private static DocumentChunk CreateChunkShell(
        Document document,
        int position,
        string text)
    {
        return new DocumentChunk(
            Guid.NewGuid(),
            document.Id,
            document.Version,
            position,
            text,
            new string('e', 64),
            ApproximateTokenCount: 1,
            "test-profile",
            "v-test",
            [],
            string.Empty,
            string.Empty,
            EmbeddingInputTokens: null,
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"));
    }

    private static IRequestHandler<ProcessIndexingJobsCommand, ProcessIndexingJobsResponse> CreateProcessIndexingJobsHandler(
        CapturingDocumentRepository repository,
        IEmbeddingClient? embeddingClient = null,
        IDocumentStorage? storage = null,
        ITextChunker? textChunker = null,
        DocumentIngestionOptions? options = null,
        EmbeddingOptions? embeddingOptions = null,
        CapturingLogger<ProcessIndexingJobsHandler>? logger = null,
        IAiRequestLogRepository? aiRequestLogRepository = null,
        IPricingRepository? pricingRepository = null,
        IUserContext? userContext = null,
        AiRequestLoggingFailureMode loggingFailureMode = AiRequestLoggingFailureMode.FailOpen)
    {
        var services = new ServiceCollection();
        services.AddTestApplication(new Microsoft.Extensions.Configuration.ConfigurationManager());
        services.AddSingleton<IDocumentIngestionRepository>(repository);
        services.AddSingleton<IDocumentStorage>(
            storage ?? new InMemoryDocumentStorage("First paragraph with enough text for chunking.\n\nSecond paragraph."));
        services.AddSingleton<IEmbeddingClient>(embeddingClient ?? new FakeEmbeddingClient());
        services.AddSingleton<ITextChunker>(
            textChunker ?? new TextChunker(Options.Create(new DocumentIngestionOptions
            {
                ChunkMaxCharacters = 48,
                ChunkOverlapCharacters = 8
            })));
        var indexingLogger = logger ?? new CapturingLogger<ProcessIndexingJobsHandler>();
        services.AddSingleton<ILogger<IndexingJobBatchProcessor>>(
            new CapturingLogger<IndexingJobBatchProcessor>(indexingLogger.Entries));
        services.AddSingleton<ILogger<IndexingEmbeddingRunner>>(
            new CapturingLogger<IndexingEmbeddingRunner>(indexingLogger.Entries));
        services.AddSingleton<ILogger<DiscardedEmbeddingObserver>>(
            new CapturingLogger<DiscardedEmbeddingObserver>(indexingLogger.Entries));
        services.AddSingleton<ILogger<IndexingJobLeaseCoordinator>>(
            new CapturingLogger<IndexingJobLeaseCoordinator>(indexingLogger.Entries));
        services.AddSingleton<ILogger<IndexingJobFailureRecorder>>(
            new CapturingLogger<IndexingJobFailureRecorder>(indexingLogger.Entries));
        services.AddSingleton<ILogger<AiModelRequestLoggingService>>(
            NullLogger<AiModelRequestLoggingService>.Instance);
        services.AddSingleton<ILogger<AiRequestLogWriter>>(
            NullLogger<AiRequestLogWriter>.Instance);
        services.AddSingleton<IAiRequestLogRepository>(
            aiRequestLogRepository ?? new CapturingAiRequestLogRepository());
        services.AddSingleton<IPricingRepository>(
            pricingRepository ?? new EmptyPricingRepository());
        services.AddSingleton<IUserContext>(
            userContext ?? new FakeUserContext("system", tenantId: null));
        services.AddSingleton(Options.Create(options ?? new DocumentIngestionOptions
        {
            MaxIndexingJobsPerPoll = 1,
            MaxIndexingAttempts = 3
        }));
        services.AddSingleton(Options.Create(embeddingOptions ?? new EmbeddingOptions
        {
            DefaultModel = "test-embedding"
        }));
        services.AddSingleton(Options.Create(new AiRequestLoggingOptions
        {
            FailureMode = loggingFailureMode
        }));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.Parse("2026-05-09T12:00:00Z")));

        return services
            .BuildServiceProvider()
            .GetRequiredService<IRequestHandler<ProcessIndexingJobsCommand, ProcessIndexingJobsResponse>>();
    }

    private static IRequestHandler<ProcessDocumentStorageCleanupCommand, ProcessDocumentStorageCleanupResponse> CreateProcessDocumentStorageCleanupHandler(
        IDocumentStorage storage,
        IDocumentStorageCleanupRepository cleanupRepository,
        IDocumentIngestionRepository repository,
        CapturingLogger<DocumentStorageCleanupRequestProcessor>? logger = null,
        DocumentIngestionOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddTestApplication(new Microsoft.Extensions.Configuration.ConfigurationManager());
        services.AddSingleton<IDocumentStorage>(storage);
        services.AddSingleton(cleanupRepository);
        services.AddSingleton<IDocumentIngestionRepository>(repository);
        services.AddSingleton<ILogger<DocumentStorageCleanupRequestProcessor>>(
            logger ?? new CapturingLogger<DocumentStorageCleanupRequestProcessor>());
        services.AddSingleton(Options.Create(options ?? new DocumentIngestionOptions
        {
            MaxIndexingJobsPerPoll = 5,
            ProcessingJobLeaseSeconds = 900,
            StorageCleanupRetryDelaySeconds = 30
        }));

        return services
            .BuildServiceProvider()
            .GetRequiredService<IRequestHandler<ProcessDocumentStorageCleanupCommand, ProcessDocumentStorageCleanupResponse>>();
    }

    private sealed class CapturingDocumentRepository : IDocumentIngestionRepository
    {
        private bool jobClaimed;

        public Document? CreatedDocument { get; private set; }

        public IndexingJob? CreatedIndexingJob { get; private set; }

        public Document? DocumentForIndexing { get; init; }

        public bool CancelGetDocumentForIndexing { get; init; }

        public IndexingJob? PendingJob { get; init; }

        public IReadOnlyCollection<DocumentChunk> CompletedChunks { get; private set; } = [];

        public IndexingJob? CompletedIndexingJob { get; private set; }

        public bool CompleteIndexingResult { get; init; } = true;

        public bool ThrowCompletionUnknownAfterCommit { get; init; }

        public bool ThrowSchemaNotReadyOnComplete { get; init; }

        public bool ThrowSchemaNotReadyOnGetDocumentForIndexing { get; init; }

        public TimeSpan? LastProcessingLeaseDuration { get; private set; }

        public int RenewProcessingLeaseCalls { get; private set; }

        public bool RenewProcessingLeaseResult { get; init; } = true;

        public int? FailRenewProcessingLeaseAfterCalls { get; init; }

        public IndexingJob? LastRenewedIndexingJob { get; private set; }

        public DateTimeOffset? LastRenewedAtUtc { get; private set; }

        public IndexingJob? LastFailedIndexingJob { get; private set; }

        public bool? LastFailureRetry { get; private set; }

        public TimeSpan? LastFailureRetryDelay { get; private set; }

        public string? LastFailureReason { get; private set; }

        public int ExpiredOrExhaustedFailures { get; init; }

        public IndexingJob? ReleasedIndexingJob { get; private set; }

        public bool ThrowIfFailureTokenCanceled { get; init; }

        public bool ThrowOnCreate { get; init; }

        public bool ThrowBeforeRepositoryCreateStarts { get; init; }

        public bool ThrowAfterCreate { get; init; }

        public bool HideMetadataOnFirstExistsCheck { get; init; }

        public bool ThrowOnDocumentExists { get; init; }

        public int DocumentExistsCalls { get; private set; }

        public bool StatusLookupCalled { get; private set; }

        public HashSet<Guid> ExistingDocumentIds { get; } = [];

        public Task CreateDocumentWithJobAsync(
            Document document,
            IndexingJob indexingJob,
            CancellationToken cancellationToken)
        {
            if (ThrowBeforeRepositoryCreateStarts)
            {
                throw new InvalidOperationException("Repository create did not start.");
            }

            if (ThrowOnCreate)
            {
                return Task.FromException(new DocumentMetadataNotCommittedException(
                    document.Id,
                    "Repository create failed before metadata was committed.",
                    new InvalidOperationException("Repository create failed.")));
            }

            CreatedDocument = document;
            CreatedIndexingJob = indexingJob;
            if (ThrowAfterCreate)
            {
                return Task.FromException(new InvalidOperationException("Repository create outcome is unknown."));
            }

            return Task.CompletedTask;
        }

        public Task<bool> DocumentExistsAsync(
            Guid documentId,
            CancellationToken cancellationToken)
        {
            DocumentExistsCalls++;
            if (ThrowOnDocumentExists)
            {
                throw new InvalidOperationException("Metadata lookup failed.");
            }

            if (HideMetadataOnFirstExistsCheck && DocumentExistsCalls == 1)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(
                CreatedDocument?.Id == documentId ||
                ExistingDocumentIds.Contains(documentId));
        }

        public Task<Document?> GetDocumentForIndexingAsync(
            Guid documentId,
            CancellationToken cancellationToken)
        {
            if (ThrowSchemaNotReadyOnGetDocumentForIndexing)
            {
                throw new DocumentIndexingSchemaNotReadyException("Schema is not ready.");
            }

            if (CancelGetDocumentForIndexing)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult(DocumentForIndexing);
        }

        public Task<DocumentIndexingStatusSnapshot?> GetDocumentStatusAsync(
            Guid documentId,
            string tenantId,
            string? userId,
            CancellationToken cancellationToken)
        {
            StatusLookupCalled = true;
            return Task.FromResult<DocumentIndexingStatusSnapshot?>(null);
        }

        public Task<IndexingJob?> ClaimNextPendingJobAsync(
            string workerId,
            TimeSpan processingLeaseDuration,
            CancellationToken cancellationToken)
        {
            LastProcessingLeaseDuration = processingLeaseDuration;
            if (jobClaimed)
            {
                return Task.FromResult<IndexingJob?>(null);
            }

            jobClaimed = true;
            return Task.FromResult(PendingJob);
        }

        public Task<int> MarkExpiredIndexingJobsFailedAsync(
            TimeSpan processingLeaseDuration,
            CancellationToken cancellationToken)
        {
            LastProcessingLeaseDuration = processingLeaseDuration;
            return Task.FromResult(ExpiredOrExhaustedFailures);
        }

        public Task<bool> RenewProcessingLeaseAsync(
            Guid documentId,
            IndexingJob indexingJob,
            CancellationToken cancellationToken)
        {
            RenewProcessingLeaseCalls++;
            LastRenewedIndexingJob = indexingJob;
            if (FailRenewProcessingLeaseAfterCalls is { } failAfter &&
                RenewProcessingLeaseCalls > failAfter)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(RenewProcessingLeaseResult);
        }

        public Task<bool> ReplaceChunksAndCompleteIndexingAsync(
            Document document,
            IndexingJob indexingJob,
            IReadOnlyCollection<DocumentChunk> chunks,
            CancellationToken cancellationToken)
        {
            CompletedChunks = chunks;
            CompletedIndexingJob = indexingJob;
            if (ThrowCompletionUnknownAfterCommit)
            {
                throw new DocumentIndexingCompletionUnknownException(
                    document.Id,
                    indexingJob.Id,
                    "Completion commit outcome is unknown.");
            }

            if (ThrowSchemaNotReadyOnComplete)
            {
                throw new DocumentIndexingSchemaNotReadyException("Schema is not ready.");
            }

            return Task.FromResult(CompleteIndexingResult);
        }

        public Task<bool> MarkIndexingFailedAsync(
            Guid documentId,
            IndexingJob indexingJob,
            string failureReason,
            bool retry,
            TimeSpan retryDelay,
            CancellationToken cancellationToken)
        {
            if (ThrowIfFailureTokenCanceled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            LastFailedIndexingJob = indexingJob;
            LastFailureRetry = retry;
            LastFailureRetryDelay = retryDelay;
            LastFailureReason = failureReason;
            return Task.FromResult(true);
        }

        public Task<bool> ReleaseProcessingJobAndRefundAttemptAsync(
            Guid documentId,
            IndexingJob indexingJob,
            CancellationToken cancellationToken)
        {
            ReleasedIndexingJob = indexingJob;
            return Task.FromResult(true);
        }
    }

    private sealed class FixedTextChunker(IReadOnlyList<DocumentChunk> chunks) : ITextChunker
    {
        public IReadOnlyList<DocumentChunk> Chunk(
            Document document,
            string text,
            DateTimeOffset createdAtUtc)
        {
            return chunks;
        }
    }

    private sealed class InMemoryDocumentStorage(
        string text,
        bool throwOnDelete = false,
        bool throwOnCommit = false,
        int deleteFailuresBeforeSuccess = 0)
        : IDocumentStorage
    {
        private int remainingDeleteFailures = deleteFailuresBeforeSuccess;

        public string? SavedPath { get; private set; }

        public List<string> DeletedPaths { get; } = [];

        public int SaveCalls { get; private set; }

        public int CommitCalls { get; private set; }

        public async Task<StoredDocument> SaveAsync(
            Guid documentId,
            string fileName,
            Stream content,
            long maxSizeBytes,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            if (maxSizeBytes > 0 && buffer.Length > maxSizeBytes)
            {
                throw new DocumentStorageLimitExceededException(maxSizeBytes);
            }

            SavedPath = $"memory://{documentId:n}/{fileName}";
            return new StoredDocument(
                SavedPath,
                new string('b', 64),
                buffer.Length);
        }

        public Task CommitAsync(
            StoredDocument document,
            CancellationToken cancellationToken)
        {
            CommitCalls++;
            if (throwOnCommit)
            {
                throw new IOException("Commit failed.");
            }

            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(
            string storagePath,
            CancellationToken cancellationToken)
        {
            Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            return Task.FromResult(stream);
        }

        public Task DeleteAsync(
            string storagePath,
            CancellationToken cancellationToken)
        {
            DeletedPaths.Add(storagePath);
            if (throwOnDelete || remainingDeleteFailures-- > 0)
            {
                throw new IOException("Delete failed.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryDocumentStorageCleanupRepository(bool throwOnRecord = false)
        : IDocumentStorageCleanupRepository
    {
        public List<DocumentStorageCleanupRequest> CleanupRequests { get; } = [];

        public List<DocumentStorageCleanupRequest> CompletedCleanupRequests { get; } = [];

        public TimeSpan? LastRetryDelay { get; private set; }

        public Task RecordAsync(
            DocumentStorageCleanupRequest request,
            CancellationToken cancellationToken)
        {
            if (throwOnRecord)
            {
                throw new IOException("Cleanup record failed.");
            }

            CleanupRequests.RemoveAll(candidate => candidate.DocumentId == request.DocumentId);
            CleanupRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<DocumentStorageCleanupRequest>> ClaimBatchAsync(
            string workerId,
            int maxRequests,
            TimeSpan processingLeaseDuration,
            CancellationToken cancellationToken)
        {
            var claimed = CleanupRequests
                .Where(static request => request.Status is
                    DocumentStorageCleanupStatus.Pending or
                    DocumentStorageCleanupStatus.Deferred)
                .Take(maxRequests)
                .Select(request => request with
                {
                    Status = DocumentStorageCleanupStatus.Processing,
                    Attempts = request.Attempts + 1,
                    WorkerId = workerId
                })
                .ToArray();

            foreach (var request in claimed)
            {
                ReplaceRequest(request);
            }

            return Task.FromResult<IReadOnlyCollection<DocumentStorageCleanupRequest>>(claimed);
        }

        public Task<bool> CompleteAsync(
            DocumentStorageCleanupRequest request,
            CancellationToken cancellationToken)
        {
            CompletedCleanupRequests.Add(request);
            CleanupRequests.RemoveAll(candidate => candidate.DocumentId == request.DocumentId);
            return Task.FromResult(true);
        }

        public Task<bool> DeferAsync(
            DocumentStorageCleanupRequest request,
            string failureReason,
            TimeSpan retryDelay,
            CancellationToken cancellationToken)
        {
            LastRetryDelay = retryDelay;
            ReplaceRequest(request with
            {
                Status = DocumentStorageCleanupStatus.Deferred,
                WorkerId = null,
                FailureReason = failureReason
            });
            return Task.FromResult(true);
        }

        public Task<bool> FailAsync(
            DocumentStorageCleanupRequest request,
            string failureReason,
            CancellationToken cancellationToken)
        {
            ReplaceRequest(request with
            {
                Status = DocumentStorageCleanupStatus.Failed,
                WorkerId = null,
                FailureReason = failureReason
            });
            return Task.FromResult(true);
        }

        private void ReplaceRequest(DocumentStorageCleanupRequest request)
        {
            var index = CleanupRequests.FindIndex(candidate => candidate.DocumentId == request.DocumentId);
            if (index >= 0)
            {
                CleanupRequests[index] = request;
                return;
            }

            CleanupRequests.Add(request);
        }
    }

    private sealed class CancelingDocumentStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(
            Guid documentId,
            string fileName,
            Stream content,
            long maxSizeBytes,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task CommitAsync(
            StoredDocument document,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<Stream> OpenReadAsync(
            string storagePath,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        public Task DeleteAsync(
            string storagePath,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

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
            CancellationToken cancellationToken)
        {
            return Task.FromResult<PricingRecord?>(null);
        }
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
