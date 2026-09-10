using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.StorageCleanup;

internal sealed class PostgresDocumentStorageCleanupTransitionStore(
    PostgresDocumentIngestionConnectionFactory connectionFactory)
{
    public async Task<bool> CompleteAsync(
        DocumentStorageCleanupRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH completed AS (
                UPDATE genai.document_storage_cleanup_requests request
                SET status = @completed_status,
                    updated_at_utc = clock_timestamp(),
                    worker_id = NULL,
                    failure_reason = NULL
                WHERE request.document_id = @document_id
                  AND request.status = @processing_status
                  AND request.worker_id = @worker_id
                RETURNING request.document_id
            )
            SELECT EXISTS (SELECT 1 FROM completed)
                OR EXISTS (
                    SELECT 1
                    FROM genai.document_storage_cleanup_requests request
                    WHERE request.document_id = @document_id
                      AND request.status = @completed_status
                );
            """, connection);
        PostgresCommandParameters.Add(command, "document_id", request.DocumentId);
        PostgresCommandParameters.Add(command, "worker_id", request.WorkerId);
        PostgresCommandParameters.Add(command, "processing_status", DocumentStorageCleanupStatus.Processing.ToString());
        PostgresCommandParameters.Add(command, "completed_status", DocumentStorageCleanupStatus.Completed.ToString());

        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<bool> DeferAsync(
        DocumentStorageCleanupRequest request,
        string failureReason,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        return await UpdateOwnedStatusAsync(
            request,
            DocumentStorageCleanupStatus.Deferred,
            failureReason,
            retryDelay,
            cancellationToken);
    }

    public async Task<bool> FailAsync(
        DocumentStorageCleanupRequest request,
        string failureReason,
        CancellationToken cancellationToken)
    {
        return await UpdateOwnedStatusAsync(
            request,
            DocumentStorageCleanupStatus.Failed,
            failureReason,
            TimeSpan.Zero,
            cancellationToken);
    }

    private async Task<bool> UpdateOwnedStatusAsync(
        DocumentStorageCleanupRequest request,
        DocumentStorageCleanupStatus status,
        string failureReason,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH cleanup_clock AS (
                SELECT clock_timestamp() AS now
            )
            UPDATE genai.document_storage_cleanup_requests request
            SET status = @status,
                available_at_utc = cleanup_clock.now + @retry_delay,
                updated_at_utc = cleanup_clock.now,
                worker_id = NULL,
                failure_reason = @failure_reason
            FROM cleanup_clock
            WHERE request.document_id = @document_id
              AND request.status = @processing_status
              AND request.worker_id = @worker_id;
            """, connection);
        PostgresCommandParameters.Add(command, "document_id", request.DocumentId);
        PostgresCommandParameters.Add(command, "worker_id", request.WorkerId);
        PostgresCommandParameters.Add(command, "processing_status", DocumentStorageCleanupStatus.Processing.ToString());
        PostgresCommandParameters.Add(command, "status", status.ToString());
        PostgresCommandParameters.Add(command, "retry_delay", retryDelay);
        PostgresCommandParameters.Add(command, "failure_reason", failureReason);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
