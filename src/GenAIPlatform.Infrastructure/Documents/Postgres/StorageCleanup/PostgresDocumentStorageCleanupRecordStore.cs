using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.StorageCleanup;

internal sealed class PostgresDocumentStorageCleanupRecordStore(
    PostgresDocumentIngestionConnectionFactory connectionFactory)
{
    public async Task RecordAsync(
        DocumentStorageCleanupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH cleanup_clock AS (
                SELECT clock_timestamp() AS now
            )
            INSERT INTO genai.document_storage_cleanup_requests (
                document_id, storage_path, staged_storage_path, content_hash, size_bytes,
                metadata_absence_proof, metadata_absence_verified_at_utc, delete_failure_reason,
                status, attempts, available_at_utc, created_at_utc, updated_at_utc, worker_id,
                failure_reason)
            SELECT
                @document_id, @storage_path, @staged_storage_path, @content_hash, @size_bytes,
                @metadata_absence_proof, @metadata_absence_verified_at_utc, @delete_failure_reason,
                @pending_status, 0, cleanup_clock.now, cleanup_clock.now, cleanup_clock.now,
                NULL, NULL
            FROM cleanup_clock
            ON CONFLICT (document_id) DO UPDATE
            SET storage_path = EXCLUDED.storage_path,
                staged_storage_path = EXCLUDED.staged_storage_path,
                content_hash = EXCLUDED.content_hash,
                size_bytes = EXCLUDED.size_bytes,
                metadata_absence_proof = EXCLUDED.metadata_absence_proof,
                metadata_absence_verified_at_utc = EXCLUDED.metadata_absence_verified_at_utc,
                delete_failure_reason = EXCLUDED.delete_failure_reason,
                status = EXCLUDED.status,
                attempts = 0,
                available_at_utc = EXCLUDED.available_at_utc,
                updated_at_utc = EXCLUDED.updated_at_utc,
                worker_id = NULL,
                failure_reason = NULL
            WHERE genai.document_storage_cleanup_requests.status NOT IN (
                @processing_status,
                @completed_status
            );
            """, connection);
        AddRequestParameters(command, request);
        PostgresCommandParameters.Add(command, "pending_status", DocumentStorageCleanupStatus.Pending.ToString());
        PostgresCommandParameters.Add(command, "processing_status", DocumentStorageCleanupStatus.Processing.ToString());
        PostgresCommandParameters.Add(command, "completed_status", DocumentStorageCleanupStatus.Completed.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddRequestParameters(
        NpgsqlCommand command,
        DocumentStorageCleanupRequest request)
    {
        PostgresCommandParameters.Add(command, "document_id", request.DocumentId);
        PostgresCommandParameters.Add(command, "storage_path", request.StoragePath);
        PostgresCommandParameters.Add(command, "staged_storage_path", request.StagedStoragePath);
        PostgresCommandParameters.Add(command, "content_hash", request.ContentHash);
        PostgresCommandParameters.Add(command, "size_bytes", request.SizeBytes);
        PostgresCommandParameters.Add(command, "metadata_absence_proof", request.MetadataAbsenceProof);
        PostgresCommandParameters.Add(command, "metadata_absence_verified_at_utc", request.MetadataAbsenceVerifiedAtUtc);
        PostgresCommandParameters.Add(command, "delete_failure_reason", request.DeleteFailureReason);
    }
}
