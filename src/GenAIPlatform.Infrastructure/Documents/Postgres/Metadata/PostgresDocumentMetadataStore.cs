using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.Metadata;

internal sealed class PostgresDocumentMetadataStore(
    PostgresDocumentIngestionConnectionFactory connectionFactory)
{
    public async Task CreateDocumentWithJobAsync(
        Document document,
        IndexingJob indexingJob,
        CancellationToken cancellationToken)
    {
        var commitStarted = false;

        try
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await InsertDocumentAsync(
                connection,
                transaction,
                document,
                cancellationToken);
            await InsertIndexingJobAsync(
                connection,
                transaction,
                indexingJob,
                cancellationToken);

            commitStarted = true;
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception exception) when (!commitStarted)
        {
            throw new DocumentMetadataNotCommittedException(
                document.Id,
                "Document metadata and indexing job were not committed.",
                exception);
        }
    }

    public async Task<bool> DocumentExistsAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM genai.documents
                WHERE id = @id
            );
            """, connection);
        PostgresCommandParameters.Add(command, "id", documentId);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<Document?> GetDocumentForIndexingAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {PostgresDocumentIngestionSql.DocumentColumns}
            FROM genai.documents
            WHERE id = @id;
            """, connection);
        PostgresCommandParameters.Add(command, "id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? PostgresDocumentMapper.Map(reader)
            : null;
    }

    private static async Task InsertDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Document document,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.documents (
                id, tenant_id, owner_user_id, file_name, title, content_type, source_extension,
                storage_path, size_bytes, content_hash, version, access_level, indexing_status,
                created_at_utc, updated_at_utc, failure_reason)
            VALUES (
                @id, @tenant_id, @owner_user_id, @file_name, @title, @content_type, @source_extension,
                @storage_path, @size_bytes, @content_hash, @version, @access_level, @indexing_status,
                @created_at_utc, @updated_at_utc, @failure_reason);
            """, connection, transaction);
        PostgresCommandParameters.AddDocumentParameters(command, document);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertIndexingJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IndexingJob indexingJob,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.indexing_jobs (
                id, document_id, status, attempts, max_attempts, created_at_utc, updated_at_utc,
                available_at_utc, started_at_utc, completed_at_utc, worker_id, failure_reason)
            VALUES (
                @id, @document_id, @status, @attempts, @max_attempts, @created_at_utc, @updated_at_utc,
                @available_at_utc, @started_at_utc, @completed_at_utc, @worker_id, @failure_reason);
            """, connection, transaction);
        PostgresCommandParameters.AddIndexingJobParameters(command, indexingJob);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
