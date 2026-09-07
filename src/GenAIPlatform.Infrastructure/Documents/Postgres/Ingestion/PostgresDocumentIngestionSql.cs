namespace GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;

internal static class PostgresDocumentIngestionSql
{
    public const string DocumentColumns = """
        id, tenant_id, owner_user_id, file_name, title, content_type, source_extension,
        storage_path, size_bytes, content_hash, version, access_level, indexing_status,
        created_at_utc, updated_at_utc, failure_reason
        """;

    public const string JobColumns = """
        id, document_id, status, attempts, max_attempts, created_at_utc, updated_at_utc,
        available_at_utc, started_at_utc, completed_at_utc, worker_id, failure_reason
        """;

    public static string PrefixJobColumns(string alias)
    {
        return string.Join(
            ", ",
            JobColumns
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(column => $"{alias}.{column}"));
    }
}
