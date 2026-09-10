using System.Runtime.InteropServices;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Evaluations.RetrievalBaseline;

/// <summary>
/// Reads the version metadata a baseline report records. Only version strings are read;
/// host names, databases, users and connection strings are never queried or reported.
/// </summary>
internal static class PostgresRetrievalBaselineEnvironmentReader
{
    private const string UnknownVersion = "unknown";

    public static async Task<RetrievalBaselineEnvironment> ReadAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        return new RetrievalBaselineEnvironment(
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            await ReadScalarAsync(connection, "SHOW server_version;", cancellationToken) ?? UnknownVersion,
            await ReadScalarAsync(
                connection,
                "SELECT extversion FROM pg_extension WHERE extname = 'vector';",
                cancellationToken));
    }

    private static async Task<string?> ReadScalarAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;
    }
}
