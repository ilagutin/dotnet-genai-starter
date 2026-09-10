using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.IndexingJobs;

internal sealed class PostgresIndexingJobLeaseStore(
    PostgresDocumentIngestionConnectionFactory connectionFactory)
{
    public async Task<bool> RenewProcessingLeaseAsync(
        Guid documentId,
        IndexingJob indexingJob,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE genai.indexing_jobs
            SET updated_at_utc = clock_timestamp()
            WHERE id = @job_id
              AND document_id = @document_id
              AND status = @processing_status
              AND worker_id = @worker_id
              AND attempts = @attempts;
            """, connection);
        AddLeaseIdentityParameters(
            command,
            documentId,
            indexingJob);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> ReleaseProcessingJobAndRefundAttemptAsync(
        Guid documentId,
        IndexingJob indexingJob,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH lease_clock AS (
                SELECT clock_timestamp() AS now
            )
            UPDATE genai.indexing_jobs
            SET status = @pending_status,
                attempts = GREATEST(attempts - 1, 0),
                updated_at_utc = lease_clock.now,
                available_at_utc = lease_clock.now,
                started_at_utc = NULL,
                completed_at_utc = NULL,
                worker_id = NULL,
                failure_reason = @failure_reason
            FROM lease_clock
            WHERE id = @job_id
              AND document_id = @document_id
              AND status = @processing_status
              AND worker_id = @worker_id
              AND attempts = @attempts;
            """, connection);
        PostgresCommandParameters.Add(command, "pending_status", IndexingJobStatus.Pending.ToString());
        PostgresCommandParameters.Add(command, "failure_reason", "Indexing job was interrupted and returned to the queue.");
        AddLeaseIdentityParameters(
            command,
            documentId,
            indexingJob);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void AddLeaseIdentityParameters(
        NpgsqlCommand command,
        Guid documentId,
        IndexingJob indexingJob)
    {
        PostgresCommandParameters.Add(command, "job_id", indexingJob.Id);
        PostgresCommandParameters.Add(command, "document_id", documentId);
        PostgresCommandParameters.Add(command, "processing_status", IndexingJobStatus.Processing.ToString());
        PostgresCommandParameters.Add(command, "worker_id", indexingJob.WorkerId);
        PostgresCommandParameters.Add(command, "attempts", indexingJob.Attempts);
    }
}
