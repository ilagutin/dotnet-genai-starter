using GenAIPlatform.Infrastructure.Migrations;

namespace GenAIPlatform.Migrations;

/// <summary>
/// Runs one verb against the schema migrator and turns its result into operator output and an
/// exit code. Failures print the sanitized migration message and its error code only.
/// </summary>
public static class MigrationCliRunner
{
    public static async Task<int> RunAsync(
        ISchemaMigrator migrator,
        string verb,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            return string.Equals(verb, MigrationCliVerbs.Status, StringComparison.OrdinalIgnoreCase)
                ? await ReportStatusAsync(migrator, output, cancellationToken)
                : await MigrateAsync(migrator, output, cancellationToken);
        }
        catch (SchemaMigrationException exception)
        {
            await error.WriteLineAsync($"{exception.Message} [{exception.ErrorCode}]");

            // A database this host was never told how to reach is a configuration error, not a
            // schema that is behind.
            return string.Equals(
                exception.ErrorCode,
                SchemaMigrationErrorCodes.NotConfigured,
                StringComparison.Ordinal)
                ? MigrationCliExitCodes.UsageOrConfiguration
                : MigrationCliExitCodes.BehindOrFailed;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync(
                "Schema migration was canceled; the migration journal was not changed.");
            return MigrationCliExitCodes.BehindOrFailed;
        }
    }

    private static async Task<int> MigrateAsync(
        ISchemaMigrator migrator,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var result = await migrator.MigrateAsync(cancellationToken);
        if (result.AdoptedLegacySchema)
        {
            await output.WriteLineAsync(
                "Adopted the existing database as the frozen v0.3.1 baseline.");
        }

        if (result.AppliedVersions.Count == 0)
        {
            await output.WriteLineAsync($"Schema is up to date at migration {result.JournalHead}.");
            return MigrationCliExitCodes.UpToDate;
        }

        await output.WriteLineAsync(
            $"Applied {result.AppliedVersions.Count} migration(s): " +
            $"{string.Join(", ", result.AppliedVersions)}. Journal head is {result.JournalHead}.");

        return MigrationCliExitCodes.UpToDate;
    }

    private static async Task<int> ReportStatusAsync(
        ISchemaMigrator migrator,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var status = await migrator.GetStatusAsync(cancellationToken);
        await output.WriteLineAsync(
            $"Journal head: {status.JournalHead ?? "(none)"}; packaged head: {status.PackagedHead}.");
        foreach (var mismatch in status.Mismatches)
        {
            await output.WriteLineAsync($"Mismatch: {mismatch}");
        }

        if (status.PendingVersions.Count > 0)
        {
            await output.WriteLineAsync($"Pending: {string.Join(", ", status.PendingVersions)}.");
        }

        if (status.IsUpToDate)
        {
            await output.WriteLineAsync("Schema is up to date.");
            return MigrationCliExitCodes.UpToDate;
        }

        await output.WriteLineAsync(
            $"Schema is not up to date. Run this host with the `{MigrationCliVerbs.Migrate}` verb.");

        return MigrationCliExitCodes.BehindOrFailed;
    }
}
