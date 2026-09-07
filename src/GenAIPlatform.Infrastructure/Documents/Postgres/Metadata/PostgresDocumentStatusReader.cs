using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.IndexingJobs;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.Metadata;

internal sealed class PostgresDocumentStatusReader(
    PostgresDocumentIngestionConnectionFactory connectionFactory)
{
    public async Task<DocumentIndexingStatusSnapshot?> GetDocumentStatusAsync(
        Guid documentId,
        string tenantId,
        string? userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var document = await ReadAuthorizedDocumentAsync(
            connection,
            documentId,
            tenantId,
            userId,
            cancellationToken);

        if (document is null)
        {
            return null;
        }

        var latestJob = await ReadLatestJobAsync(
            connection,
            document.Id,
            cancellationToken);
        var chunkCount = await ReadChunkCountAsync(
            connection,
            document,
            cancellationToken);

        return new DocumentIndexingStatusSnapshot(document, latestJob, chunkCount);
    }

    private static async Task<Document?> ReadAuthorizedDocumentAsync(
        NpgsqlConnection connection,
        Guid documentId,
        string tenantId,
        string? userId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {PostgresDocumentIngestionSql.DocumentColumns}
            FROM genai.documents
            WHERE id = @id
              AND tenant_id = @tenant_id
              AND (access_level = @public_access_level OR owner_user_id = @user_id);
            """, connection);
        PostgresCommandParameters.Add(command, "id", documentId);
        PostgresCommandParameters.Add(command, "tenant_id", tenantId);
        PostgresCommandParameters.Add(command, "user_id", userId);
        PostgresCommandParameters.Add(command, "public_access_level", DocumentAccessLevel.TenantPublic.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? PostgresDocumentMapper.Map(reader)
            : null;
    }

    private static async Task<IndexingJob?> ReadLatestJobAsync(
        NpgsqlConnection connection,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {PostgresDocumentIngestionSql.JobColumns}
            FROM genai.indexing_jobs
            WHERE document_id = @document_id
            ORDER BY created_at_utc DESC
            LIMIT 1;
            """, connection);
        PostgresCommandParameters.Add(command, "document_id", documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? PostgresIndexingJobMapper.Map(reader)
            : null;
    }

    private static async Task<int> ReadChunkCountAsync(
        NpgsqlConnection connection,
        Document document,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COUNT(*)::int
            FROM genai.document_chunks
            WHERE document_id = @document_id
              AND document_version = @document_version;
            """, connection);
        PostgresCommandParameters.Add(command, "document_id", document.Id);
        PostgresCommandParameters.Add(command, "document_version", document.Version);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }
}
