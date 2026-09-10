using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Lease;
using GenAIPlatform.Domain.Documents;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Embedding;

internal sealed class IndexingEmbeddingRunner(
    IEmbeddingClient embeddingClient,
    IndexingJobLeaseCoordinator leaseCoordinator,
    DiscardedEmbeddingObserver discardedEmbeddingObserver,
    TimeProvider timeProvider)
{
    public async Task<EmbeddingResponse> CreateWithLeaseRenewalAsync(
        Document document,
        IndexingJob indexingJob,
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        using var providerCancellation = OwnedCancellation.CreateLinked(cancellationToken);
        Task<EmbeddingResponse>? embeddingTask = null;
        var embeddingResultSelected = false;
        var embeddingStarted = 0L;

        try
        {
            embeddingStarted = timeProvider.GetTimestamp();
            embeddingTask = embeddingClient.CreateEmbeddingAsync(request, providerCancellation.Token);
            while (true)
            {
                var completed = await Task.WhenAny(
                    embeddingTask,
                    Task.Delay(leaseCoordinator.GetLeaseRenewalInterval(), cancellationToken));

                if (completed == embeddingTask)
                {
                    embeddingResultSelected = true;
                    return await embeddingTask;
                }

                await leaseCoordinator.RenewOrThrowAsync(
                    document,
                    indexingJob,
                    cancellationToken);
            }
        }
        catch
        {
            await AbandonAndObserveAsync(
                document,
                indexingJob,
                request,
                embeddingTask,
                providerCancellation,
                embeddingResultSelected,
                embeddingStarted);

            throw;
        }
    }

    private async Task AbandonAndObserveAsync(
        Document document,
        IndexingJob indexingJob,
        EmbeddingRequest request,
        Task<EmbeddingResponse>? embeddingTask,
        OwnedCancellation providerCancellation,
        bool embeddingResultSelected,
        long embeddingStarted)
    {
        if (embeddingTask is null || embeddingResultSelected)
        {
            return;
        }

        if (!embeddingTask.IsCompleted)
        {
            await discardedEmbeddingObserver.CancelAndObserveAsync(
                document,
                indexingJob,
                request,
                embeddingTask,
                providerCancellation.Transfer(),
                embeddingStarted);
            return;
        }

        await discardedEmbeddingObserver.ObserveCompletedAsync(
            document,
            indexingJob,
            request,
            embeddingTask,
            embeddingStarted: embeddingStarted);
    }
}
