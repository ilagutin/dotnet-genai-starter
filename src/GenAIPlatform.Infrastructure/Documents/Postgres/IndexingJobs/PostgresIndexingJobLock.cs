using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.IndexingJobs;

internal sealed class PostgresIndexingJobLock
{
    public async Task<bool> TryLockProcessingJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        IndexingJob indexingJob,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1
            FROM genai.indexing_jobs
            WHERE id = @job_id
              AND document_id = @document_id
              AND status = @processing_status
              AND worker_id = @worker_id
              AND attempts = @attempts
            FOR UPDATE;
            """, connection, transaction);
        PostgresCommandParameters.Add(command, "job_id", indexingJob.Id);
        PostgresCommandParameters.Add(command, "document_id", documentId);
        PostgresCommandParameters.Add(command, "processing_status", IndexingJobStatus.Processing.ToString());
        PostgresCommandParameters.Add(command, "worker_id", indexingJob.WorkerId);
        PostgresCommandParameters.Add(command, "attempts", indexingJob.Attempts);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
