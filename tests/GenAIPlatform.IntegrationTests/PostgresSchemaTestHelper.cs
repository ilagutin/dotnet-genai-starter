using Npgsql;

namespace GenAIPlatform.IntegrationTests;

/// <summary>
/// Integration fixtures use the same authoritative upgrade path as an operator: the privileged
/// extension step, then the packaged migration runner. Frozen v0.3.1 SQL stays available under
/// <c>Fixtures/legacy-v0.3.1</c> so legacy-upgrade tests exercise a genuine old database.
/// </summary>
internal static class PostgresSchemaTestHelper
{
    public const string LegacyFixtureDirectoryName = "legacy-v0.3.1";

    public static readonly string[] LegacyV031ScriptNames =
    [
        "001-enable-pgvector.sql",
        "002-document-ingestion.sql",
        "003-pgvector-retrieval.sql",
        "004-observability-cost.sql",
        "005-evaluations.sql",
        "006-tool-audit.sql",
        "007-document-storage-cleanup.sql"
    ];

    public static async Task EnsureSchemaAsync(string connectionString)
    {
        await EnableVectorExtensionAsync(connectionString);

        using var host = new SchemaMigrationTestHost(connectionString);
        await host.Migrator.MigrateAsync(CancellationToken.None);
    }

    /// <summary>
    /// Restores a shared test database after a test left a partial or legacy schema behind. The
    /// runner refuses to adopt an unrecognized schema, so cleanup drops it first and migrates a
    /// fresh one.
    /// </summary>
    public static async Task RebuildSchemaAsync(string connectionString)
    {
        await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
        await EnsureSchemaAsync(connectionString);
    }

    public static async Task EnableVectorExtensionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "CREATE EXTENSION IF NOT EXISTS vector;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Applies frozen v0.3.1 initialization scripts. Use it to build a genuine legacy or partial
    /// database; it never writes a migration journal.
    /// </summary>
    public static async Task ApplyInitScriptsAsync(
        string connectionString,
        params string[] scriptNames)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var fixtureDirectory = FindLegacyFixtureDirectory();
        foreach (var scriptName in scriptNames)
        {
            await ExecuteScriptAsync(connection, Path.Combine(fixtureDirectory, scriptName));
        }
    }

    public static Task ApplyLegacyV031SchemaAsync(string connectionString)
    {
        return ApplyInitScriptsAsync(connectionString, LegacyV031ScriptNames);
    }

    public static string FindLegacyFixtureDirectory()
    {
        foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "tests",
                    "GenAIPlatform.IntegrationTests",
                    "Fixtures",
                    LegacyFixtureDirectoryName);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Frozen v0.3.1 SQL fixture directory was not found.");
    }

    private static async Task ExecuteScriptAsync(
        NpgsqlConnection connection,
        string scriptPath)
    {
        var schemaSql = await File.ReadAllTextAsync(scriptPath);
        await using var command = new NpgsqlCommand(schemaSql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
