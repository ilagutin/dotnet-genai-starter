using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Postgres;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Retrieval;

internal sealed partial class PostgresRagSearchExecutor(
    PostgresRagConnectionFactory connectionFactory,
    RagVectorSearchErrorMapper errorMapper,
    ILogger<PostgresRagSearchExecutor> logger)
{
    public async Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
        RagVectorSearchQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = CreateSearchCommand(
                connection,
                query);

            var chunks = new List<RetrievedDocumentChunk>(query.TopK);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                chunks.Add(new RetrievedDocumentChunk(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetDouble(7)));
            }

            return chunks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RagVectorSearchException)
        {
            throw;
        }
        catch (PostgresException exception)
        {
            throw LogInfrastructureFailure(errorMapper.MapPostgres(exception), exception);
        }
        catch (NpgsqlException exception)
        {
            throw LogInfrastructureFailure(errorMapper.Unavailable(exception), exception);
        }
        catch (TimeoutException exception)
        {
            throw LogInfrastructureFailure(errorMapper.Unavailable(exception), exception);
        }
    }

    private RagVectorSearchException LogInfrastructureFailure(RagVectorSearchException mapped, Exception exception)
    {
        LogSearchFailed(logger, mapped.ErrorCode, exception.GetType().Name);
        return mapped;
    }

    [LoggerMessage(
        EventId = 5001,
        EventName = "RagSearchInfrastructureFailure",
        Level = LogLevel.Warning,
        Message = "RAG search infrastructure failure: {ErrorCode} ({ExceptionType})")]
    private static partial void LogSearchFailed(ILogger logger, string? errorCode, string exceptionType);

    private static NpgsqlCommand CreateSearchCommand(
        NpgsqlConnection connection,
        RagVectorSearchQuery query)
    {
        var distanceExpression = PostgresRagSearchSql.GetDistanceExpression(query.QueryEmbedding.Count);
        var documentFilter = query.DocumentIds.Count > 0
            ? "AND document.id = ANY(@document_ids)"
            : string.Empty;
        var command = new NpgsqlCommand($"""
            WITH query_embedding AS (
                SELECT @query_embedding::vector AS embedding
            )
            SELECT
                document.id,
                chunk.id,
                chunk.document_version,
                chunk.position,
                document.title,
                document.file_name,
                chunk.text,
                (1 - ({distanceExpression}))::double precision AS similarity_score
            FROM genai.document_chunks chunk
            INNER JOIN genai.documents document
                ON document.id = chunk.document_id
            CROSS JOIN query_embedding
            WHERE document.tenant_id = @tenant_id
              AND document.indexing_status = @indexed_status
              AND (document.access_level = @tenant_public_access_level OR document.owner_user_id = @user_id)
              AND chunk.document_version = document.version
              AND chunk.embedding_vector IS NOT NULL
              AND vector_norm(chunk.embedding_vector) > 0
              AND chunk.embedding_dimensions = @embedding_dimensions
              AND chunk.embedding_model = @embedding_model
              AND chunk.embedding_provider = @embedding_provider
              AND (1 - ({distanceExpression})) >= @min_similarity_score
              {documentFilter}
            ORDER BY
                {distanceExpression},
                document.created_at_utc,
                document.id,
                chunk.position,
                chunk.id
            LIMIT @top_k;
            """, connection);

        AddParameters(command, query);
        return command;
    }

    private static void AddParameters(
        NpgsqlCommand command,
        RagVectorSearchQuery query)
    {
        command.Parameters.AddWithValue("query_embedding", PostgresVectorParameter.From(query.QueryEmbedding));
        command.Parameters.AddWithValue("tenant_id", query.TenantId);
        command.Parameters.AddWithValue("indexed_status", DocumentIndexingStatus.Indexed.ToString());
        command.Parameters.AddWithValue("tenant_public_access_level", DocumentAccessLevel.TenantPublic.ToString());
        command.Parameters.AddWithValue("user_id", query.UserId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("embedding_dimensions", query.QueryEmbedding.Count);
        command.Parameters.AddWithValue("embedding_model", query.EmbeddingModel);
        command.Parameters.AddWithValue("embedding_provider", query.EmbeddingProvider);
        command.Parameters.AddWithValue("min_similarity_score", query.MinSimilarityScore);
        command.Parameters.AddWithValue("top_k", query.TopK);

        if (query.DocumentIds.Count > 0)
        {
            command.Parameters.AddWithValue("document_ids", query.DocumentIds.ToArray());
        }
    }
}
