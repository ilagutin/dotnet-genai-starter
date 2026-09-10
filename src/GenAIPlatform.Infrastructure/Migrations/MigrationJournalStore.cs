using GenAIPlatform.Infrastructure.Postgres;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Durable version/checksum journal plus the failure-attempt record. Creating both tables is
/// itself idempotent and is the first thing a migration run does.
/// </summary>
internal sealed class MigrationJournalStore(PostgresDataSourceProvider dataSourceProvider)
{
    private const string EnsureJournalSql = """
        CREATE SCHEMA IF NOT EXISTS genai;

        CREATE TABLE IF NOT EXISTS genai.schema_migrations (
            version text PRIMARY KEY,
            name text NOT NULL,
            checksum text NOT NULL CHECK (checksum ~ '^[a-f0-9]{64}$'),
            applied_at_utc timestamptz NOT NULL,
            applied_by text NOT NULL,
            duration_ms bigint NOT NULL CHECK (duration_ms >= 0),
            adopted boolean NOT NULL
        );

        CREATE TABLE IF NOT EXISTS genai.schema_migration_attempts (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            version text NOT NULL,
            started_at_utc timestamptz NOT NULL,
            finished_at_utc timestamptz NOT NULL,
            outcome text NOT NULL CHECK (outcome IN ('rolled-back', 'unknown')),
            error_type text NOT NULL,
            error_summary text NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_schema_migration_attempts_version
            ON genai.schema_migration_attempts (version, started_at_utc DESC);
        """;

    private const string InsertJournalRowSql = """
        INSERT INTO genai.schema_migrations (
            version, name, checksum, applied_at_utc, applied_by, duration_ms, adopted)
        VALUES (@version, @name, @checksum, @applied_at_utc, session_user, @duration_ms, @adopted)
        ON CONFLICT (version) DO NOTHING;
        """;

    public async Task<bool> JournalExistsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('genai.schema_migrations') IS NOT NULL;",
            connection);
        var exists = await command.ExecuteScalarAsync(cancellationToken);

        return exists is true;
    }

    /// <summary>
    /// Whether the journal already describes this database. A journal table that exists but holds
    /// no row proves nothing: an interrupted first run can leave exactly that, so the caller must
    /// classify the schema itself rather than trust the table's existence. The existence probe
    /// stays a separate statement because PostgreSQL resolves the table name while parsing.
    /// </summary>
    public async Task<bool> HasEntriesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await JournalExistsAsync(connection, cancellationToken))
        {
            return false;
        }

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM genai.schema_migrations);",
            connection);
        var hasEntries = await command.ExecuteScalarAsync(cancellationToken);

        return hasEntries is true;
    }

    public async Task EnsureJournalAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(EnsureJournalSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MigrationJournalEntry>> ReadAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT version, name, checksum, adopted
            FROM genai.schema_migrations
            ORDER BY version COLLATE "C";
            """, connection);

        var entries = new List<MigrationJournalEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new MigrationJournalEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3)));
        }

        return entries;
    }

    public async Task InsertAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MigrationScript script,
        DateTimeOffset appliedAtUtc,
        long durationMilliseconds,
        bool adopted,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(InsertJournalRowSql, connection, transaction);
        command.Parameters.AddWithValue("version", script.Version);
        command.Parameters.AddWithValue("name", script.Name);
        command.Parameters.AddWithValue("checksum", script.Checksum);
        command.Parameters.AddWithValue("applied_at_utc", appliedAtUtc);
        command.Parameters.AddWithValue("duration_ms", durationMilliseconds);
        command.Parameters.AddWithValue("adopted", adopted);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the journal and writes the adopted baseline rows in one transaction on the caller's
    /// locked connection. Table creation and the rows that give it meaning commit together, so a
    /// run that dies in between cannot leave an empty journal that a later run would mistake for a
    /// fresh database.
    /// </summary>
    public async Task CreateJournalWithAdoptedRowsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<MigrationScript> scripts,
        DateTimeOffset adoptedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(EnsureJournalSql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var script in scripts)
        {
            await InsertAppliedAsync(
                connection,
                transaction,
                script,
                adoptedAtUtc,
                durationMilliseconds: 0,
                adopted: true,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Records a failed or uncertain attempt on a separate connection, because the migration
    /// transaction is already aborted when this runs. Returns a sanitized description when the
    /// record itself could not be written, so the caller can surface both facts.
    /// </summary>
    public async Task<string?> TryRecordAttemptAsync(
        MigrationAttemptRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
            await EnsureJournalAsync(connection, cancellationToken);
            await using var command = new NpgsqlCommand("""
                INSERT INTO genai.schema_migration_attempts (
                    version, started_at_utc, finished_at_utc, outcome, error_type, error_summary)
                VALUES (@version, @started_at_utc, @finished_at_utc, @outcome, @error_type, @error_summary);
                """, connection);
            command.Parameters.AddWithValue("version", record.Version);
            command.Parameters.AddWithValue("started_at_utc", record.StartedAtUtc);
            command.Parameters.AddWithValue("finished_at_utc", record.FinishedAtUtc);
            command.Parameters.AddWithValue(
                "outcome",
                MigrationAttemptOutcomeMapping.ToPersistenceValue(record.Outcome));
            command.Parameters.AddWithValue("error_type", record.ErrorType);
            command.Parameters.AddWithValue("error_summary", record.ErrorSummary);
            await command.ExecuteNonQueryAsync(cancellationToken);

            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return SchemaMigrationErrorMapper.Describe(exception);
        }
    }

    public async Task<string?> ReadHeadAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await JournalExistsAsync(connection, cancellationToken))
        {
            return null;
        }

        await using var command = new NpgsqlCommand("""
            SELECT version
            FROM genai.schema_migrations
            ORDER BY version COLLATE "C" DESC
            LIMIT 1;
            """, connection);
        var head = await command.ExecuteScalarAsync(cancellationToken);

        return head as string;
    }
}
