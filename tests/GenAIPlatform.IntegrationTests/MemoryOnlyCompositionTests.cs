using GenAIPlatform.Application.Core;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Knowledge;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GenAIPlatform.IntegrationTests;

public sealed class MemoryOnlyCompositionTests
{
    [Fact]
    public void CoreAndKnowledge_CanBuildWithoutChatModelServices()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationCore(configuration);
        services.AddKnowledgeApplication(configuration);
        services.AddSingleton<MemoryOnlyUserContext>();
        services.AddSingleton<IUserContext>(serviceProvider =>
            serviceProvider.GetRequiredService<MemoryOnlyUserContext>());
        services.AddSingleton<IBackgroundUserContext>(serviceProvider =>
            serviceProvider.GetRequiredService<MemoryOnlyUserContext>());
        services.AddSingleton<IDocumentStorage, MemoryOnlyDocumentStorage>();
        services.AddSingleton<MemoryOnlyDocumentIngestionRepository>();
        services.AddSingleton<IDocumentMetadataRepository>(provider => provider.GetRequiredService<MemoryOnlyDocumentIngestionRepository>());
        services.AddSingleton<IIndexingJobRepository>(provider => provider.GetRequiredService<MemoryOnlyDocumentIngestionRepository>());
        services.AddSingleton<IDocumentStorageCleanupRepository, MemoryOnlyDocumentStorageCleanupRepository>();
        services.AddSingleton<IEmbeddingClient, MemoryOnlyEmbeddingClient>();
        services.AddSingleton<IRagVectorSearchStore, MemoryOnlyRagVectorSearchStore>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>());
        Assert.Empty(scope.ServiceProvider.GetServices<IAiModelClient>());
    }

    private sealed class MemoryOnlyUserContext : IBackgroundUserContext
    {
        public bool IsAuthenticated => true;

        public string? UserId => "system";

        public string? TenantId => null;

        public IReadOnlyCollection<string> Roles => ["system"];

        public IReadOnlyCollection<string> Groups => [];
    }

    private sealed class MemoryOnlyDocumentStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(
            Guid documentId,
            string fileName,
            Stream content,
            long maxSizeBytes,
            CancellationToken cancellationToken) =>
            Task.FromException<StoredDocument>(new NotSupportedException());

        public Task CommitAsync(
            StoredDocument document,
            CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());

        public Task<Stream> OpenReadAsync(
            string storagePath,
            CancellationToken cancellationToken) =>
            Task.FromException<Stream>(new NotSupportedException());

        public Task DeleteAsync(
            string storagePath,
            CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());
    }

    private sealed class MemoryOnlyDocumentIngestionRepository : IDocumentMetadataRepository, IIndexingJobRepository
    {
        public Task CreateDocumentWithJobAsync(
            Document document,
            IndexingJob indexingJob,
            CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());

        public Task<bool> DocumentExistsAsync(
            Guid documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<Document?> GetDocumentForIndexingAsync(
            Guid documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<Document?>(null);

        public Task<DocumentIndexingStatusSnapshot?> GetDocumentStatusAsync(
            Guid documentId,
            string tenantId,
            string? userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<DocumentIndexingStatusSnapshot?>(null);

        public Task<IndexingJob?> ClaimNextPendingJobAsync(
            string workerId,
            TimeSpan processingLeaseDuration,
            CancellationToken cancellationToken) =>
            Task.FromResult<IndexingJob?>(null);

        public Task<int> MarkExpiredIndexingJobsFailedAsync(
            TimeSpan processingLeaseDuration,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<bool> RenewProcessingLeaseAsync(
            Guid documentId,
            IndexingJob indexingJob,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> ReplaceChunksAndCompleteIndexingAsync(
            Document document,
            IndexingJob indexingJob,
            IReadOnlyCollection<DocumentChunk> chunks,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> MarkIndexingFailedAsync(
            Guid documentId,
            IndexingJob indexingJob,
            string failureReason,
            bool retry,
            TimeSpan retryDelay,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> ReleaseProcessingJobAndRefundAttemptAsync(
            Guid documentId,
            IndexingJob indexingJob,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class MemoryOnlyDocumentStorageCleanupRepository : IDocumentStorageCleanupRepository
    {
        public Task RecordAsync(
            DocumentStorageCleanupRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());

        public Task<IReadOnlyCollection<DocumentStorageCleanupRequest>> ClaimBatchAsync(
            string workerId,
            int maxRequests,
            TimeSpan processingLeaseDuration,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<DocumentStorageCleanupRequest>>([]);

        public Task<bool> CompleteAsync(
            DocumentStorageCleanupRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> DeferAsync(
            DocumentStorageCleanupRequest request,
            string failureReason,
            TimeSpan retryDelay,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> FailAsync(
            DocumentStorageCleanupRequest request,
            string failureReason,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class MemoryOnlyEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResponse([0], "memory-only", "test", 0, request.CorrelationId));
    }

    private sealed class MemoryOnlyRagVectorSearchStore : IRagVectorSearchStore
    {
        public Task CheckReadinessAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
            RagVectorSearchQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetrievedDocumentChunk>>([]);
    }
}
