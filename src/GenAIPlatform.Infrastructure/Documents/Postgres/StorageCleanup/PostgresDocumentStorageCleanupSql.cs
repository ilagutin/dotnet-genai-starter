namespace GenAIPlatform.Infrastructure.Documents.Postgres.StorageCleanup;

internal static class PostgresDocumentStorageCleanupSql
{
    public const string CleanupColumns = """
        document_id, storage_path, staged_storage_path, content_hash, size_bytes,
        metadata_absence_proof, metadata_absence_verified_at_utc, delete_failure_reason,
        status, attempts, available_at_utc, created_at_utc, updated_at_utc, worker_id,
        failure_reason
        """;

    public static string PrefixColumns(string alias)
    {
        return string.Join(
            ", ",
            CleanupColumns
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(column => $"{alias}.{column}"));
    }
}
