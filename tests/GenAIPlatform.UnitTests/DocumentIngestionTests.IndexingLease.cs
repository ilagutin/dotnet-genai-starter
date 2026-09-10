using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Infrastructure.Observability.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed partial class DocumentIngestionTests
{
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

}
