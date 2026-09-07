using GenAIPlatform.Domain.Documents;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.Shared;

internal static class PostgresCommandParameters
{
    public static void AddDocumentParameters(
        NpgsqlCommand command,
        Document document)
    {
        Add(command, "id", document.Id);
        Add(command, "tenant_id", document.TenantId);
        Add(command, "owner_user_id", document.OwnerUserId);
        Add(command, "file_name", document.FileName);
        Add(command, "title", document.Title);
        Add(command, "content_type", document.ContentType);
        Add(command, "source_extension", document.SourceExtension);
        Add(command, "storage_path", document.StoragePath);
        Add(command, "size_bytes", document.SizeBytes);
        Add(command, "content_hash", document.ContentHash);
        Add(command, "version", document.Version);
        Add(command, "access_level", document.AccessLevel.ToString());
        Add(command, "indexing_status", document.IndexingStatus.ToString());
        Add(command, "created_at_utc", document.CreatedAtUtc);
        Add(command, "updated_at_utc", document.UpdatedAtUtc);
        Add(command, "failure_reason", document.FailureReason);
    }

    public static void AddIndexingJobParameters(
        NpgsqlCommand command,
        IndexingJob indexingJob)
    {
        Add(command, "id", indexingJob.Id);
        Add(command, "document_id", indexingJob.DocumentId);
        Add(command, "status", indexingJob.Status.ToString());
        Add(command, "attempts", indexingJob.Attempts);
        Add(command, "max_attempts", indexingJob.MaxAttempts);
        Add(command, "created_at_utc", indexingJob.CreatedAtUtc);
        Add(command, "updated_at_utc", indexingJob.UpdatedAtUtc);
        Add(command, "available_at_utc", indexingJob.AvailableAtUtc);
        Add(command, "started_at_utc", indexingJob.StartedAtUtc);
        Add(command, "completed_at_utc", indexingJob.CompletedAtUtc);
        Add(command, "worker_id", indexingJob.WorkerId);
        Add(command, "failure_reason", indexingJob.FailureReason);
    }

    public static void Add(
        NpgsqlCommand command,
        string name,
        object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
