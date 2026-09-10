using Npgsql;

namespace GenAIPlatform.IntegrationTests;

/// <summary>
/// Representative rows for a populated frozen v0.3.1 database: a document with a chunk, an
/// indexing job, an evaluation run with a case result, an AI request log, a tool audit row and a
/// durable storage cleanup request. Adoption must preserve every one of them.
/// </summary>
internal static class LegacyV031SeedData
{
    public static readonly string[] SeededTables =
    [
        "documents",
        "indexing_jobs",
        "document_chunks",
        "evaluation_runs",
        "evaluation_case_results",
        "ai_request_logs",
        "tool_audit_logs",
        "document_storage_cleanup_requests"
    ];

    private const string SeedSql = """
        INSERT INTO genai.documents (
            id, tenant_id, owner_user_id, file_name, title, content_type, source_extension,
            storage_path, size_bytes, content_hash, version, access_level, indexing_status,
            created_at_utc, updated_at_utc, failure_reason)
        VALUES (
            '11111111-1111-1111-1111-111111111111', 'tenant-legacy', 'legacy-user',
            'legacy-notes.md', 'Legacy Notes', 'text/markdown', '.md',
            'tenant-legacy/legacy-notes.md', 42,
            '1111111111111111111111111111111111111111111111111111111111111111',
            1, 'TenantPublic', 'Indexed',
            '2026-01-01T00:00:00Z', '2026-01-01T00:05:00Z', NULL);

        INSERT INTO genai.indexing_jobs (
            id, document_id, status, attempts, max_attempts, created_at_utc, updated_at_utc,
            available_at_utc, started_at_utc, completed_at_utc, worker_id, failure_reason)
        VALUES (
            '22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111',
            'Completed', 1, 3, '2026-01-01T00:00:00Z', '2026-01-01T00:05:00Z',
            '2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', '2026-01-01T00:05:00Z',
            'legacy-worker', NULL);

        INSERT INTO genai.document_chunks (
            id, document_id, document_version, position, text, text_hash,
            approximate_token_count, chunking_profile, chunking_profile_version, embedding_model,
            embedding_provider, embedding_dimensions, embedding_input_tokens, embedding_values,
            created_at_utc)
        VALUES (
            '33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111',
            1, 0, 'legacy chunk text',
            '3333333333333333333333333333333333333333333333333333333333333333',
            4, 'plain-text', 'v1', 'test-embedding', 'test-provider', 2, 4,
            ARRAY[1, 0]::real[], '2026-01-01T00:04:00Z');

        INSERT INTO genai.evaluation_runs (
            run_id, tenant_id, user_id, dataset_version, runner_version, prompt_version, model,
            model_settings, retrieval_configuration, status, started_at_utc, completed_at_utc)
        VALUES (
            '44444444-4444-4444-4444-444444444444', 'tenant-legacy', 'legacy-user',
            'sample-v1', '0.3.1', 'rag-answer-v1', 'mock-chat',
            '{"temperature":0.2}'::jsonb, '{"topK":5}'::jsonb, 'Succeeded',
            '2026-01-02T00:00:00Z', '2026-01-02T00:01:00Z');

        INSERT INTO genai.evaluation_case_results (
            run_id, case_id, name, status, answer, retrieved_count, retrieval_hit, latency_ms,
            estimated_cost, cost_currency, error_code, error_message, checks)
        VALUES (
            '44444444-4444-4444-4444-444444444444', 'case-1', 'Legacy case', 'Passed',
            'legacy answer', 2, true, 12, 0, 'USD', NULL, NULL, '[]'::jsonb);

        INSERT INTO genai.ai_request_logs (
            request_id, api_version, user_id, tenant_id, correlation_id, provider, model, status,
            error_code, latency_ms, input_tokens, output_tokens, total_tokens, embedding_tokens,
            estimated_cost, cost_currency, prompt_template_name, prompt_template_version,
            prompt_template_content_hash, retrieval_latency_ms, retrieved_document_ids,
            citation_references, created_at_utc)
        VALUES (
            '55555555-5555-5555-5555-555555555555', 'v1', 'legacy-user', 'tenant-legacy',
            'legacy-correlation-1', 'mock', 'mock-chat', 'Succeeded', NULL, 15, 10, 5, 15, NULL,
            0, 'USD', 'rag-answer', 'v1',
            '5555555555555555555555555555555555555555555555555555555555555555', 3,
            ARRAY['11111111-1111-1111-1111-111111111111']::uuid[], ARRAY['doc-1']::text[],
            '2026-01-03T00:00:00Z');

        INSERT INTO genai.tool_audit_logs (
            id, conversation_id, tenant_id, user_id, correlation_id, tool_call_id, tool_name,
            schema_version, policy_version, validation_status, policy_decision, approval_state,
            execution_status, arguments, output, error_code, error_message, created_at_utc)
        VALUES (
            '66666666-6666-6666-6666-666666666666', '77777777-7777-7777-7777-777777777777',
            'tenant-legacy', 'legacy-user', 'legacy-correlation-2', 'call-1',
            'get_current_user_profile', 'v1', 'tool-policy-v1', 'Valid', 'Allowed', 'NotRequired',
            'Succeeded', '{}'::jsonb, '{"userId":"legacy-user"}'::jsonb, NULL, NULL,
            '2026-01-04T00:00:00Z');

        INSERT INTO genai.document_storage_cleanup_requests (
            document_id, storage_path, staged_storage_path, content_hash, size_bytes,
            metadata_absence_proof, metadata_absence_verified_at_utc, delete_failure_reason,
            status, attempts, available_at_utc, created_at_utc, updated_at_utc, worker_id,
            failure_reason)
        VALUES (
            '88888888-8888-8888-8888-888888888888', 'tenant-legacy/orphan.md', NULL,
            '8888888888888888888888888888888888888888888888888888888888888888', 17,
            'metadata-rollback-verified', '2026-01-05T00:00:00Z', 'storage delete failed',
            'Pending', 1, '2026-01-05T00:10:00Z', '2026-01-05T00:00:00Z', '2026-01-05T00:00:00Z',
            NULL, NULL);
        """;

    public static async Task SeedAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(SeedSql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Reads representative column values, not just row counts, so a migration that recreated or
    /// truncated and refilled a table cannot pass as a preserved one.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string?>> ReadSampleValuesAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT title FROM genai.documents
                 WHERE id = '11111111-1111-1111-1111-111111111111'),
                (SELECT text_hash FROM genai.document_chunks
                 WHERE id = '33333333-3333-3333-3333-333333333333'),
                (SELECT status FROM genai.evaluation_runs
                 WHERE run_id = '44444444-4444-4444-4444-444444444444');
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["documents.title"] = reader.IsDBNull(0) ? null : reader.GetString(0),
            ["document_chunks.text_hash"] = reader.IsDBNull(1) ? null : reader.GetString(1),
            ["evaluation_runs.status"] = reader.IsDBNull(2) ? null : reader.GetString(2)
        };
    }

    public static async Task<IReadOnlyDictionary<string, long>> ReadRowCountsAsync(
        string connectionString)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var tableName in SeededTables)
        {
            counts[tableName] = await SchemaMigrationTestSupport.CountRowsAsync(
                connectionString,
                tableName);
        }

        return counts;
    }
}
