using Npgsql;

namespace GenAIPlatform.IntegrationTests;

/// <summary>
/// Holds the migration advisory lock on a dedicated session, so a test can observe what a second
/// runner does while the lock is taken.
/// </summary>
internal sealed class HeldMigrationAdvisoryLock : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;

    private HeldMigrationAdvisoryLock(NpgsqlConnection connection)
    {
        this.connection = connection;
    }

    public static async Task<HeldMigrationAdvisoryLock> AcquireAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtext('genai.schema_migrations')::bigint);",
            connection);
        await command.ExecuteScalarAsync();

        return new HeldMigrationAdvisoryLock(connection);
    }

    public async Task ReleaseAsync()
    {
        await using (var command = new NpgsqlCommand(
            "SELECT pg_advisory_unlock(hashtext('genai.schema_migrations')::bigint);",
            connection))
        {
            await command.ExecuteScalarAsync();
        }

        await connection.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
    }
}
