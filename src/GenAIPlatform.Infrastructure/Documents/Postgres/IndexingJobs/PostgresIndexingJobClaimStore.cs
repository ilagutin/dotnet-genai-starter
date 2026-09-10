using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.IndexingJobs;

internal sealed class PostgresIndexingJobClaimStore(
    PostgresDocumentIngestionConnectionFactory connectionFactory,
    PostgresIndexingSchemaReadiness schemaReadiness)
{
    private const string ProcessingTimeoutFailureReason = "Indexing job timed out while processing.";
    private const string MaxAttemptsFailureReason = "Indexing job reached maximum attempts.";

    public async Task<IndexingJob?> ClaimNextPendingJobAsync(
        string workerId,
        TimeSpan processingLeaseDuration,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await schemaReadiness.EnsureReadyAsync(connection, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            WITH lease_clock AS (
                SELECT clock_timestamp() AS now
            ),
            next_job AS (
                SELECT id
                FROM genai.indexing_jobs, lease_clock
                WHERE (status = @pending_status
                       AND available_at_utc <= lease_clock.now
                       AND attempts < max_attempts)
                   OR (status = @processing_status
                       AND updated_at_utc <= lease_clock.now - @processing_lease_duration
                       AND attempts < max_attempts)
                ORDER BY created_at_utc
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE genai.indexing_jobs job
            SET status = @processing_status,
                attempts = attempts + 1,
                updated_at_utc = lease_clock.now,
                started_at_utc = lease_clock.now,
                completed_at_utc = NULL,
                worker_id = @worker_id,
                failure_reason = NULL
            FROM next_job, lease_clock
            WHERE job.id = next_job.id
            RETURNING {PostgresDocumentIngestionSql.PrefixJobColumns("job")};
            """, connection);
        PostgresCommandParameters.Add(command, "pending_status", IndexingJobStatus.Pending.ToString());
        PostgresCommandParameters.Add(command, "processing_status", IndexingJobStatus.Processing.ToString());
        PostgresCommandParameters.Add(command, "processing_lease_duration", processingLeaseDuration);
        PostgresCommandParameters.Add(command, "worker_id", workerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? PostgresIndexingJobMapper.Map(reader)
            : null;
    }

    public async Task<int> MarkExpiredIndexingJobsFailedAsync(
        TimeSpan processingLeaseDuration,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await schemaReadiness.EnsureReadyAsync(connection, cancellationToken);
        // Keep the terminal job updates and document status fan-out atomic across the cleanup pass.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var failedCount = 0;
        failedCount += await MarkExhaustedPendingJobsFailedAsync(
            connection,
            transaction,
            cancellationToken);
        failedCount += await MarkExpiredProcessingJobsFailedAsync(
            connection,
            transaction,
            processingLeaseDuration,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return failedCount;
    }

    private static async Task<int> MarkExhaustedPendingJobsFailedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH lease_clock AS (
                SELECT clock_timestamp() AS now
            ),
            failed_jobs AS (
                UPDATE genai.indexing_jobs job
                SET status = @failed_status,
                    updated_at_utc = lease_clock.now,
                    completed_at_utc = lease_clock.now,
                    failure_reason = @failure_reason
                FROM lease_clock
                WHERE job.status = @pending_status
                  AND job.available_at_utc <= lease_clock.now
                  AND job.attempts >= job.max_attempts
                RETURNING job.document_id
            ),
            failed_documents AS (
                UPDATE genai.documents document
                SET indexing_status = @failed_indexing_status,
                    updated_at_utc = lease_clock.now,
                    failure_reason = @failure_reason
                FROM failed_jobs, lease_clock
                WHERE document.id = failed_jobs.document_id
                RETURNING document.id
            )
            SELECT COUNT(*)::int
            FROM failed_documents;
            """, connection, transaction);
        PostgresCommandParameters.Add(command, "pending_status", IndexingJobStatus.Pending.ToString());
        PostgresCommandParameters.Add(command, "failed_status", IndexingJobStatus.Failed.ToString());
        PostgresCommandParameters.Add(command, "failed_indexing_status", DocumentIndexingStatus.Failed.ToString());
        PostgresCommandParameters.Add(command, "failure_reason", MaxAttemptsFailureReason);

        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<int> MarkExpiredProcessingJobsFailedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TimeSpan processingLeaseDuration,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH lease_clock AS (
                SELECT clock_timestamp() AS now
            ),
            failed_jobs AS (
                UPDATE genai.indexing_jobs job
                SET status = @failed_status,
                    updated_at_utc = lease_clock.now,
                    completed_at_utc = lease_clock.now,
                    failure_reason = @failure_reason
                FROM lease_clock
                WHERE job.status = @processing_status
                  AND job.updated_at_utc <= lease_clock.now - @processing_lease_duration
                  AND job.attempts >= job.max_attempts
                RETURNING job.document_id
            ),
            failed_documents AS (
                UPDATE genai.documents document
                SET indexing_status = @failed_indexing_status,
                    updated_at_utc = lease_clock.now,
                    failure_reason = @failure_reason
                FROM failed_jobs, lease_clock
                WHERE document.id = failed_jobs.document_id
                RETURNING document.id
            )
            SELECT COUNT(*)::int
            FROM failed_documents;
            """, connection, transaction);
        PostgresCommandParameters.Add(command, "processing_status", IndexingJobStatus.Processing.ToString());
        PostgresCommandParameters.Add(command, "failed_status", IndexingJobStatus.Failed.ToString());
        PostgresCommandParameters.Add(command, "failed_indexing_status", DocumentIndexingStatus.Failed.ToString());
        PostgresCommandParameters.Add(command, "processing_lease_duration", processingLeaseDuration);
        PostgresCommandParameters.Add(command, "failure_reason", ProcessingTimeoutFailureReason);

        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }
}
