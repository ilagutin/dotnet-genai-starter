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
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Observability.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed partial class DocumentIngestionTests
{
    private static IApplicationDispatcher CreateUploadDocumentHandler(
        IDocumentStorage storage,
        IDocumentMetadataRepository repository,
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
        IDocumentMetadataRepository repository,
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
        services.AddSingleton<IDocumentMetadataRepository>(repository);
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
        services.AddSingleton<IDocumentMetadataRepository>(repository);
        services.AddSingleton<IIndexingJobRepository>(repository);
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
        IDocumentMetadataRepository repository,
        CapturingLogger<DocumentStorageCleanupRequestProcessor>? logger = null,
        DocumentIngestionOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddTestApplication(new Microsoft.Extensions.Configuration.ConfigurationManager());
        services.AddSingleton<IDocumentStorage>(storage);
        services.AddSingleton(cleanupRepository);
        services.AddSingleton<IDocumentMetadataRepository>(repository);
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

    private sealed class CapturingDocumentRepository : IDocumentMetadataRepository, IIndexingJobRepository
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
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

}
