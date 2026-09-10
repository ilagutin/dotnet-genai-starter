using GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Evaluations.RetrievalBaseline;

/// <summary>
/// Writes the benchmark corpus inside one transaction. Every statement is scoped to the
/// tenants the corpus names, which the dataset validator and
/// <see cref="PostgresRetrievalBaselineCorpusStore" /> have both already constrained to
/// the fixed benchmark tenant namespace, so a baseline run can never delete ordinary
/// documents.
/// </summary>
internal static class PostgresRetrievalBaselineCorpusWriter
{
    public static async Task ReplaceAsync(
        NpgsqlConnection connection,
        RetrievalBaselineCorpus corpus,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await DeleteTenantDocumentsAsync(connection, transaction, corpus.TenantIds, cancellationToken);
        foreach (var document in corpus.Documents)
        {
            await InsertDocumentAsync(connection, transaction, document, cancellationToken);
            foreach (var chunk in document.Chunks)
            {
                await PostgresDocumentChunkWriter.InsertChunkAsync(
                    connection,
                    transaction,
                    RetrievalBaselineRowFactory.CreateChunk(document, chunk),
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task DeleteTenantDocumentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> tenantIds,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            DELETE FROM genai.documents
            WHERE tenant_id = ANY(@tenant_ids);
            """, connection, transaction);
        PostgresCommandParameters.Add(command, "tenant_ids", tenantIds.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDocumentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RetrievalBaselineCorpusDocument document,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.documents (
                id, tenant_id, owner_user_id, file_name, title, content_type, source_extension,
                storage_path, size_bytes, content_hash, version, access_level, indexing_status,
                created_at_utc, updated_at_utc, failure_reason)
            VALUES (
                @id, @tenant_id, @owner_user_id, @file_name, @title, @content_type, @source_extension,
                @storage_path, @size_bytes, @content_hash, @version, @access_level, @indexing_status,
                @created_at_utc, @updated_at_utc, @failure_reason);
            """, connection, transaction);
        PostgresCommandParameters.AddDocumentParameters(
            command,
            RetrievalBaselineRowFactory.CreateDocument(document));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
