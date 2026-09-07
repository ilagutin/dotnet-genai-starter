using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Embedding;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Failure;
using GenAIPlatform.Domain.Documents;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;

internal sealed class IndexingJobProcessor(
    IDocumentIngestionRepository repository,
    IndexingChunkEmbeddingWorkflow embeddingWorkflow,
    IndexingJobFailureRecorder failureRecorder)
{
    public async Task<IndexingJobProcessingResult> ProcessAsync(
        IndexingJob indexingJob,
        string? correlationId,
        IndexingAttemptState attemptState,
        CancellationToken cancellationToken)
    {
        var document = await repository.GetDocumentForIndexingAsync(
            indexingJob.DocumentId,
            cancellationToken);

        if (document is null)
        {
            return await failureRecorder.RecordMissingDocumentAsync(
                indexingJob,
                cancellationToken);
        }

        var chunks = await embeddingWorkflow.CreateEmbeddedChunksAsync(
            document,
            indexingJob,
            correlationId,
            attemptState,
            cancellationToken);

        var completed = await repository.ReplaceChunksAndCompleteIndexingAsync(
            document,
            indexingJob,
            chunks,
            cancellationToken);

        return completed
            ? IndexingJobProcessingResult.Indexed
            : IndexingJobProcessingResult.None;
    }
}
