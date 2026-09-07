using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.StorageCleanup;

internal sealed class PostgresDocumentStorageCleanupClaimStore(
    PostgresDocumentIngestionConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyCollection<DocumentStorageCleanupRequest>> ClaimBatchAsync(
        string workerId,
        int maxRequests,
        TimeSpan processingLeaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        if (maxRequests <= 0)
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            WITH lease_clock AS (
                SELECT clock_timestamp() AS now
            ),
            next_requests AS (
                SELECT request.document_id
                FROM genai.document_storage_cleanup_requests request, lease_clock
                WHERE ((request.status IN (@pending_status, @deferred_status)
                        AND request.available_at_utc <= lease_clock.now)
                       OR (request.status = @processing_status
                           AND request.available_at_utc <= lease_clock.now))
                ORDER BY request.created_at_utc, request.document_id
                FOR UPDATE SKIP LOCKED
                LIMIT @max_requests
            )
            UPDATE genai.document_storage_cleanup_requests request
            SET status = @processing_status,
                attempts = request.attempts + 1,
                available_at_utc = lease_clock.now + @processing_lease_duration,
                updated_at_utc = lease_clock.now,
                worker_id = @worker_id,
                failure_reason = NULL
            FROM next_requests, lease_clock
            WHERE request.document_id = next_requests.document_id
            RETURNING {PostgresDocumentStorageCleanupSql.PrefixColumns("request")};
            """, connection);
        PostgresCommandParameters.Add(command, "pending_status", DocumentStorageCleanupStatus.Pending.ToString());
        PostgresCommandParameters.Add(command, "processing_status", DocumentStorageCleanupStatus.Processing.ToString());
        PostgresCommandParameters.Add(command, "deferred_status", DocumentStorageCleanupStatus.Deferred.ToString());
        PostgresCommandParameters.Add(command, "worker_id", workerId);
        PostgresCommandParameters.Add(command, "max_requests", maxRequests);
        PostgresCommandParameters.Add(command, "processing_lease_duration", processingLeaseDuration);

        var cleanupRequests = new List<DocumentStorageCleanupRequest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cleanupRequests.Add(PostgresDocumentStorageCleanupMapper.Map(reader));
        }

        return cleanupRequests;
    }
}
