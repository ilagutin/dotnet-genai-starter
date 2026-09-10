using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.IndexingJobs;

internal sealed class PostgresIndexingJobFailureStore(
    PostgresDocumentIngestionConnectionFactory connectionFactory,
    PostgresIndexingJobLock jobLock)
{
    public async Task<bool> MarkIndexingFailedAsync(
        Guid documentId,
        IndexingJob indexingJob,
        string failureReason,
        bool retry,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await jobLock.TryLockProcessingJobAsync(
                connection,
                transaction,
                documentId,
                indexingJob,
                cancellationToken))
        {
            return false;
        }

        if (retry)
        {
            await MarkRetryAsync(
                connection,
                transaction,
                indexingJob,
                TruncateFailureReason(failureReason),
                retryDelay,
                cancellationToken);
        }
        else
        {
            await MarkTerminalFailureAsync(
                connection,
                transaction,
                documentId,
                indexingJob,
                TruncateFailureReason(failureReason),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task MarkRetryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IndexingJob indexingJob,
        string failureReason,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH lease_clock AS (
                SELECT clock_timestamp() AS now
            )
            UPDATE genai.indexing_jobs
            SET status = @job_status,
                updated_at_utc = lease_clock.now,
                available_at_utc = lease_clock.now + @retry_delay,
                started_at_utc = NULL,
                completed_at_utc = NULL,
                worker_id = NULL,
                failure_reason = @failure_reason
            FROM lease_clock
            WHERE id = @job_id;
            """, connection, transaction);
        PostgresCommandParameters.Add(command, "job_status", IndexingJobStatus.Pending.ToString());
        PostgresCommandParameters.Add(command, "retry_delay", retryDelay);
        PostgresCommandParameters.Add(command, "failure_reason", failureReason);
        PostgresCommandParameters.Add(command, "job_id", indexingJob.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkTerminalFailureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        IndexingJob indexingJob,
        string failureReason,
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
                    failure_reason = @failure_reason
                FROM lease_clock
                WHERE id = @document_id
                RETURNING id
            )
            UPDATE genai.indexing_jobs job
            SET status = @job_status,
                updated_at_utc = lease_clock.now,
                completed_at_utc = lease_clock.now,
                failure_reason = @failure_reason
            FROM lease_clock, updated_document
            WHERE job.id = @job_id;
            """, connection, transaction);
        PostgresCommandParameters.Add(command, "indexing_status", DocumentIndexingStatus.Failed.ToString());
        PostgresCommandParameters.Add(command, "job_status", IndexingJobStatus.Failed.ToString());
        PostgresCommandParameters.Add(command, "failure_reason", failureReason);
        PostgresCommandParameters.Add(command, "document_id", documentId);
        PostgresCommandParameters.Add(command, "job_id", indexingJob.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string TruncateFailureReason(string failureReason)
    {
        var normalized = string.IsNullOrWhiteSpace(failureReason)
            ? "Indexing failed."
            : failureReason.Trim();

        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }
}
