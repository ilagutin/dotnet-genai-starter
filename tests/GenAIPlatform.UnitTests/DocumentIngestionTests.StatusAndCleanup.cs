using GenAIPlatform.Application.Knowledge.Documents;

namespace GenAIPlatform.UnitTests;

public sealed partial class DocumentIngestionTests
{
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

}
