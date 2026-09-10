using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Domain.Evaluations.Retrieval;
using GenAIPlatform.Domain.Exceptions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;

/// <summary>
/// Embeds the frozen benchmark corpus with the configured embedding client. Chunk text is
/// only passed to the embedding port; it is never logged or returned in a report.
/// </summary>
internal sealed class RetrievalBaselineCorpusBuilder(
    IEmbeddingClient embeddingClient,
    IOptions<EmbeddingOptions> embeddingOptions)
{
    public async Task<RetrievalBaselineCorpusBuildResult> BuildAsync(
        RetrievalBaselineDataset dataset,
        CancellationToken cancellationToken)
    {
        var model = embeddingOptions.Value.DefaultModel;
        var documents = new List<RetrievalBaselineCorpusDocument>(dataset.Corpus.Count);
        var documentIdsByRowId = new Dictionary<Guid, string>();
        string? resolvedProvider = null;
        string? resolvedModel = null;
        var dimensions = 0;

        foreach (var document in dataset.Corpus)
        {
            var chunks = new List<RetrievalBaselineCorpusChunk>(document.Chunks.Count);
            var position = 0;
            foreach (var chunk in document.Chunks)
            {
                var embedding = await embeddingClient.CreateEmbeddingAsync(
                    new EmbeddingRequest(chunk.Text, model, $"retrieval-baseline-{chunk.Id}"),
                    cancellationToken);
                EmbeddingVectorValidator.EnsureValidCosineVector(embedding);

                resolvedProvider ??= embedding.Provider;
                resolvedModel ??= embedding.Model;
                dimensions = EnsureStableDimensions(dimensions, embedding.Vector.Count, chunk.Id);

                chunks.Add(new RetrievalBaselineCorpusChunk(
                    chunk.Id,
                    RetrievalBaselineIdFactory.CreateChunkId(chunk.Id),
                    chunk.DocumentVersion ?? document.Version,
                    position++,
                    chunk.Text,
                    embedding.Vector,
                    embedding.InputTokens));
            }

            var rowId = RetrievalBaselineIdFactory.CreateDocumentId(document.Id);
            documentIdsByRowId[rowId] = document.Id;
            documents.Add(new RetrievalBaselineCorpusDocument(
                document.Id,
                rowId,
                document.TenantId,
                document.OwnerUserId,
                document.AccessLevel,
                document.Version,
                document.Title,
                document.FileName,
                document.EmbeddingProvider ?? resolvedProvider!,
                document.EmbeddingModel ?? resolvedModel!,
                chunks));
        }

        return new RetrievalBaselineCorpusBuildResult(
            new RetrievalBaselineCorpus(CollectTenantIds(dataset), documents),
            resolvedProvider!,
            resolvedModel!,
            dimensions,
            documentIdsByRowId);
    }

    private static IReadOnlyList<string> CollectTenantIds(RetrievalBaselineDataset dataset)
    {
        return dataset.Corpus
            .Select(static document => document.TenantId)
            .Concat(dataset.Queries.Select(static query => query.TenantId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static int EnsureStableDimensions(
        int current,
        int observed,
        string chunkId)
    {
        if (current != 0 && current != observed)
        {
            throw new EvaluationValidationException(
                $"Retrieval baseline chunk '{chunkId}' produced an embedding with unexpected dimensions.");
        }

        return observed;
    }
}
