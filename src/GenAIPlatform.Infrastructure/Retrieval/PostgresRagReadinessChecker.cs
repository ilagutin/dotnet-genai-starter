using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Infrastructure.Migrations;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Retrieval;

internal sealed class PostgresRagReadinessChecker(
    PostgresRagConnectionFactory connectionFactory,
    MigrationJournalHeadReader journalHeadReader,
    RagVectorSearchErrorMapper errorMapper)
{
    public async Task CheckReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                WITH vector_type AS (
                    SELECT to_regtype('vector') AS oid
                )
                SELECT
                    EXISTS (
                        SELECT 1
                        FROM pg_extension
                        WHERE extname = 'vector'
                    ) AS has_vector_extension,
                    to_regclass('genai.document_chunks') IS NOT NULL AS has_document_chunks_table,
                    EXISTS (
                        SELECT 1
                        FROM pg_attribute attribute
                        INNER JOIN pg_class class
                            ON class.oid = attribute.attrelid
                        INNER JOIN pg_namespace namespace
                            ON namespace.oid = class.relnamespace
                        CROSS JOIN vector_type
                        WHERE namespace.nspname = 'genai'
                          AND class.relname = 'document_chunks'
                          AND attribute.attname = 'embedding_vector'
                          AND attribute.attisdropped = false
                          AND vector_type.oid IS NOT NULL
                          AND attribute.atttypid = vector_type.oid
                    ) AS has_embedding_vector_column;
                """, connection);

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken) ||
                    !reader.GetBoolean(0) ||
                    !reader.GetBoolean(1) ||
                    !reader.GetBoolean(2))
                {
                    throw RagVectorSearchErrorMapper.SchemaNotReady();
                }
            }

            // Retrieval must not answer from a database that a newer build has migrations for.
            if (!await journalHeadReader.IsJournalCurrentAsync(connection, cancellationToken))
            {
                throw RagVectorSearchErrorMapper.SchemaNotReady();
            }
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
            throw errorMapper.MapPostgres(exception);
        }
        catch (NpgsqlException exception)
        {
            throw errorMapper.Unavailable(exception);
        }
        catch (TimeoutException exception)
        {
            throw errorMapper.Unavailable(exception);
        }
        catch (ArgumentException exception)
        {
            throw errorMapper.Unavailable(exception);
        }
        catch (InvalidOperationException exception)
        {
            throw errorMapper.QueryFailed(
                "RAG retrieval readiness check failed.",
                exception);
        }
    }
}
