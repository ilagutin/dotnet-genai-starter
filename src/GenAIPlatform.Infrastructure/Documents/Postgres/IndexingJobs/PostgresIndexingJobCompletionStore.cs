using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.IndexingJobs;

internal sealed class PostgresIndexingJobCompletionStore(
    PostgresDocumentIngestionConnectionFactory connectionFactory,
    PostgresIndexingSchemaReadiness schemaReadiness,
    PostgresIndexingJobLock jobLock)
{
    public async Task<bool> ReplaceChunksAndCompleteIndexingAsync(
        Document document,
        IndexingJob indexingJob,
        IReadOnlyCollection<DocumentChunk> chunks,
        CancellationToken cancellationToken)
    {
        var completionCommitStarted = false;

        try
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await schemaReadiness.EnsureReadyAsync(connection, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            if (!await jobLock.TryLockProcessingJobAsync(
                    connection,
                    transaction,
                    document.Id,
                    indexingJob,
                    cancellationToken))
            {
                return false;
            }

            await ReplaceChunksAsync(
                connection,
                transaction,
                document,
                chunks,
                cancellationToken);
            await CompleteJobAsync(
                connection,
                transaction,
                document.Id,
                indexingJob.Id,
                cancellationToken);

            completionCommitStarted = true;
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (completionCommitStarted)
        {
            throw new DocumentIndexingCompletionUnknownException(
                document.Id,
                indexingJob.Id,
                "Document indexing completion outcome is unknown.",
                exception);
        }
    }

    private static async Task ReplaceChunksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Document document,
        IReadOnlyCollection<DocumentChunk> chunks,
        CancellationToken cancellationToken)
    {
        await DeleteExistingChunksAsync(
            connection,
            transaction,
            document,
            cancellationToken);

        foreach (var chunk in chunks.OrderBy(static chunk => chunk.Position))
        {
            await PostgresDocumentChunkWriter.InsertChunkAsync(
                connection,
                transaction,
                chunk,
                cancellationToken);
        }
    }

    private static async Task DeleteExistingChunksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Document document,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            DELETE FROM genai.document_chunks
            WHERE document_id = @document_id
              AND document_version = @document_version;
            """, connection, transaction);
        PostgresCommandParameters.Add(command, "document_id", document.Id);
        PostgresCommandParameters.Add(command, "document_version", document.Version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        Guid indexingJobId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH lease_clock AS (
                SELECT clock_timestamp() AS now
            ),
            updated_document AS (
                UPDATE genai.documents
                SET indexing_status = @indexing_status,
                    updated_at_utc = lease_clock.now,
                    failure_reason = NULL
                FROM lease_clock
                WHERE id = @document_id
                RETURNING id
            )
            UPDATE genai.indexing_jobs job
            SET status = @job_status,
                updated_at_utc = lease_clock.now,
                completed_at_utc = lease_clock.now,
                failure_reason = NULL
            FROM lease_clock, updated_document
            WHERE job.id = @job_id;
            """, connection, transaction);
        PostgresCommandParameters.Add(command, "indexing_status", DocumentIndexingStatus.Indexed.ToString());
        PostgresCommandParameters.Add(command, "job_status", IndexingJobStatus.Completed.ToString());
        PostgresCommandParameters.Add(command, "document_id", documentId);
        PostgresCommandParameters.Add(command, "job_id", indexingJobId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
