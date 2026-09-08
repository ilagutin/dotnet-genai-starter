using GenAIPlatform.Application.Knowledge.Retrieval;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.Infrastructure.Retrieval;

internal sealed class PostgresRagVectorSearchStore
    : IRagVectorSearchStore
{
    private readonly RagVectorSearchQueryValidator queryValidator;
    private readonly PostgresRagReadinessChecker readinessChecker;
    private readonly PostgresRagSearchExecutor searchExecutor;

    public PostgresRagVectorSearchStore(
        PostgresRagConnectionFactory connectionFactory,
        ILogger<PostgresRagSearchExecutor> logger)
    {
        var errorMapper = new RagVectorSearchErrorMapper();

        queryValidator = new RagVectorSearchQueryValidator();
        readinessChecker = new PostgresRagReadinessChecker(
            connectionFactory,
            errorMapper);
        searchExecutor = new PostgresRagSearchExecutor(
            connectionFactory,
            errorMapper,
            logger);
    }

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
