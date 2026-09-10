using GenAIPlatform.Infrastructure.Migrations;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

/// <summary>
/// Shared database probes for schema migration tests: canonical schema snapshots, journal and
/// attempt contents, and the advisory lock a runner must never leave behind.
/// </summary>
internal static class SchemaMigrationTestSupport
{
    /// <summary>
    /// Counts advisory locks for the migration key only. pg_advisory_lock stores a bigint key as
    /// two 32-bit halves, so the comparison rebuilds the unsigned 64-bit value.
    /// </summary>
    private const string MigrationAdvisoryLockCountSql = """
        SELECT count(*)
        FROM pg_locks
        WHERE locktype = 'advisory'
          AND (classid::bigint::numeric * 4294967296 + objid::bigint::numeric)
              = ((hashtext('genai.schema_migrations')::numeric + 18446744073709551616::numeric)
                 % 18446744073709551616::numeric);
        """;

    /// <summary>
    /// A table in the <c>genai</c> schema that no packaged migration owns, standing in for a
    /// database this platform shares with something else.
    /// </summary>
    public const string ForeignTableName = "some_other_app";

    public static async Task CreateForeignTableAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE SCHEMA IF NOT EXISTS genai;
            CREATE TABLE genai.{ForeignTableName} (id uuid PRIMARY KEY);
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task ResetSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DROP SCHEMA IF EXISTS genai CASCADE;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<IReadOnlyList<string>> ReadSchemaSnapshotAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        return await new SchemaFingerprintReader().ReadAsync(connection, CancellationToken.None);
    }

    public static async Task<IReadOnlyList<MigrationJournalEntry>> ReadJournalAsync(
        string connectionString)
    {
        if (!await TableExistsAsync(connectionString, "schema_migrations"))
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT version, name, checksum, adopted
            FROM genai.schema_migrations
            ORDER BY version COLLATE "C";
            """, connection);
        var entries = new List<MigrationJournalEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new MigrationJournalEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3)));
        }

        return entries;
    }

    public static async Task<IReadOnlyList<(string Version, string Outcome, string ErrorSummary)>>
        ReadAttemptsAsync(string connectionString)
    {
        if (!await TableExistsAsync(connectionString, "schema_migration_attempts"))
        {
            return [];
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT version, outcome, error_summary
            FROM genai.schema_migration_attempts
            ORDER BY id;
            """, connection);
        var attempts = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            attempts.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return attempts;
    }

    public static async Task<bool> TableExistsAsync(string connectionString, string tableName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('genai.' || @table_name) IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue("table_name", tableName);

        return await command.ExecuteScalarAsync() is true;
    }

    public static async Task<bool> ColumnExistsAsync(
        string connectionString,
        string tableName,
        string columnName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'genai'
                  AND table_name = @table_name
                  AND column_name = @column_name);
            """, connection);
        command.Parameters.AddWithValue("table_name", tableName);
        command.Parameters.AddWithValue("column_name", columnName);

        return await command.ExecuteScalarAsync() is true;
    }

    /// <summary>
    /// Chunks that carry no pgvector embedding. After migration 0007 the column is NOT NULL, so
    /// this is zero on a migrated database and a non-zero result means the migration did not run.
    /// </summary>
    public static async Task<long> CountUnvectorizedChunksAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM genai.document_chunks WHERE embedding_vector IS NULL;",
            connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<long> CountMigrationAdvisoryLocksAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(MigrationAdvisoryLockCountSql, connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<long> CountRowsAsync(string connectionString, string tableName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM genai.{tableName};",
            connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    public static IReadOnlyList<MigrationScriptDefinition> PackagedDefinitions()
    {
        return
        [
            .. EmbeddedMigrationCatalogFactory
                .Create()
                .Scripts
                .Select(static script =>
                    new MigrationScriptDefinition(script.Version, script.Name, script.Sql))
        ];
    }
}
