using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed partial class DocumentIngestionTests
{
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

}
