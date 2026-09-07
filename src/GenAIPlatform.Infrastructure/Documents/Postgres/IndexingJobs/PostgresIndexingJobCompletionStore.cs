using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using GenAIPlatform.Infrastructure.Postgres;
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
            await InsertChunkAsync(
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

    private static async Task InsertChunkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DocumentChunk chunk,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.document_chunks (
                id, document_id, document_version, position, text, text_hash,
                approximate_token_count, chunking_profile, chunking_profile_version,
                embedding_model, embedding_provider, embedding_dimensions, embedding_input_tokens,
                embedding_values, embedding_vector, created_at_utc)
            VALUES (
                @id, @document_id, @document_version, @position, @text, @text_hash,
                @approximate_token_count, @chunking_profile, @chunking_profile_version,
                @embedding_model, @embedding_provider, @embedding_dimensions, @embedding_input_tokens,
                @embedding_values, @embedding_vector::vector, @created_at_utc);
            """, connection, transaction);
        PostgresCommandParameters.Add(command, "id", chunk.Id);
        PostgresCommandParameters.Add(command, "document_id", chunk.DocumentId);
        PostgresCommandParameters.Add(command, "document_version", chunk.DocumentVersion);
        PostgresCommandParameters.Add(command, "position", chunk.Position);
        PostgresCommandParameters.Add(command, "text", chunk.Text);
        PostgresCommandParameters.Add(command, "text_hash", chunk.TextHash);
        PostgresCommandParameters.Add(command, "approximate_token_count", chunk.ApproximateTokenCount);
        PostgresCommandParameters.Add(command, "chunking_profile", chunk.ChunkingProfile);
        PostgresCommandParameters.Add(command, "chunking_profile_version", chunk.ChunkingProfileVersion);
        PostgresCommandParameters.Add(command, "embedding_model", chunk.EmbeddingModel);
        PostgresCommandParameters.Add(command, "embedding_provider", chunk.EmbeddingProvider);
        PostgresCommandParameters.Add(command, "embedding_dimensions", chunk.EmbeddingDimensions);
        PostgresCommandParameters.Add(command, "embedding_input_tokens", chunk.EmbeddingInputTokens);
        PostgresCommandParameters.Add(command, "embedding_values", chunk.Embedding.ToArray());
        PostgresCommandParameters.Add(command, "embedding_vector", PostgresVectorParameter.From(chunk.Embedding));
        PostgresCommandParameters.Add(command, "created_at_utc", chunk.CreatedAtUtc);
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
