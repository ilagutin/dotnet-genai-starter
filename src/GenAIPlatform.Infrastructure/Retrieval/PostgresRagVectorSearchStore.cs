using GenAIPlatform.Application.Knowledge.Retrieval;

namespace GenAIPlatform.Infrastructure.Retrieval;

internal sealed class PostgresRagVectorSearchStore(
    RagVectorSearchQueryValidator queryValidator,
    PostgresRagReadinessChecker readinessChecker,
    PostgresRagSearchExecutor searchExecutor) : IRagVectorSearchStore
{
    public async Task CheckReadinessAsync(CancellationToken cancellationToken)
    {
        await readinessChecker.CheckReadinessAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
        RagVectorSearchQuery query,
        CancellationToken cancellationToken)
    {
        queryValidator.EnsureValid(query);

        return await searchExecutor.SearchAsync(
            query,
            cancellationToken);
    }
}
