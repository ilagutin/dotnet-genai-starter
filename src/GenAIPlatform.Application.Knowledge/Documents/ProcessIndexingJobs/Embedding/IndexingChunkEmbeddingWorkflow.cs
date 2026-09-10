using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Failure;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Lease;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Embedding;

internal sealed class IndexingChunkEmbeddingWorkflow(
    IDocumentStorage documentStorage,
    ITextExtractor textExtractor,
    ITextChunker textChunker,
    IOptions<EmbeddingOptions> embeddingOptions,
    TimeProvider timeProvider,
    IndexingJobLeaseCoordinator leaseCoordinator,
    IndexingEmbeddingRunner embeddingRunner)
{
    public async Task<IReadOnlyList<DocumentChunk>> CreateEmbeddedChunksAsync(
        Document document,
        IndexingJob indexingJob,
        string? correlationId,
        IndexingAttemptState attemptState,
        CancellationToken cancellationToken)
    {
        await leaseCoordinator.RenewOrThrowAsync(
            document,
            indexingJob,
            cancellationToken);

        attemptState.MarkAttemptConsumed();
        await using var content = await documentStorage.OpenReadAsync(
            document.StoragePath,
            cancellationToken);

        var extractedText = await textExtractor.ExtractAsync(
            document,
            content,
            cancellationToken);

        await leaseCoordinator.RenewOrThrowAsync(
            document,
            indexingJob,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(extractedText))
        {
            throw new DocumentValidationException("Document text is empty.");
        }

        return await EmbedChunksAsync(
            document,
            indexingJob,
            textChunker.Chunk(document, extractedText, timeProvider.GetUtcNow()),
            correlationId,
            attemptState,
            cancellationToken);
    }

    private async Task<IReadOnlyList<DocumentChunk>> EmbedChunksAsync(
        Document document,
        IndexingJob indexingJob,
        IReadOnlyList<DocumentChunk> chunkShells,
        string? correlationId,
        IndexingAttemptState attemptState,
        CancellationToken cancellationToken)
    {
        if (chunkShells.Count == 0)
        {
            throw new DocumentValidationException("Document text produced no chunks.");
        }

        var embeddedChunks = new List<DocumentChunk>(chunkShells.Count);
        foreach (var chunk in chunkShells)
        {
            await leaseCoordinator.RenewOrThrowAsync(
                document,
                indexingJob,
                cancellationToken);

            attemptState.MarkAttemptConsumed();
            var embeddingInput = GetEmbeddingInput(chunk);
            var embeddingResponse = await embeddingRunner.CreateWithLeaseRenewalAsync(
                document,
                indexingJob,
                new EmbeddingRequest(
                    embeddingInput,
                    embeddingOptions.Value.DefaultModel,
                    correlationId),
                cancellationToken);

            EmbeddingVectorValidator.EnsureValidCosineVector(embeddingResponse);

            embeddedChunks.Add(chunk with
            {
                Embedding = embeddingResponse.Vector.ToArray(),
                EmbeddingModel = embeddingResponse.Model,
                EmbeddingProvider = embeddingResponse.Provider,
                EmbeddingInputTokens = embeddingResponse.InputTokens
            });

            await leaseCoordinator.RenewOrThrowAsync(
                document,
                indexingJob,
                cancellationToken);
        }

        return embeddedChunks;
    }

    private string GetEmbeddingInput(DocumentChunk chunk)
    {
        var maxCharacters = embeddingOptions.Value.MaxInputCharacters;
        if (maxCharacters <= 0)
        {
            throw new InvalidOperationException(
                "Embedding max input characters must be greater than zero.");
        }

        if (chunk.Text.Length > maxCharacters)
        {
            throw new DocumentValidationException(
                "Document chunk text exceeds the configured embedding input limit.");
        }

        return chunk.Text;
    }
}
