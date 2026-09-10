using Npgsql;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Decides what a database without a migration journal is. An empty schema takes the fresh path;
/// an exact frozen v0.3.1 fingerprint is adopted; a partial or unrecognized schema fails with the
/// first differing fingerprint line. Nothing is dropped or altered to make a schema match.
/// </summary>
internal sealed class LegacySchemaAdopter(SchemaFingerprintReader fingerprintReader)
{
    public async Task<LegacySchemaDecision> DecideAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var actualLines = await fingerprintReader.ReadAsync(connection, cancellationToken);
        if (actualLines.Count == 0)
        {
            return LegacySchemaDecision.Fresh;
        }

        var difference = FindFirstDifference(LegacyV031Baseline.SchemaLines, actualLines);
        if (difference is null)
        {
            return LegacySchemaDecision.AdoptLegacyBaseline;
        }

        throw new SchemaMigrationException(
            $"This database has {MigrationNames.SchemaName} tables but no {MigrationNames.JournalTable} " +
            $"journal, and it does not match the frozen {MigrationNames.LegacySourceVersion} schema: " +
            $"{difference} Only an empty database or an unmodified {MigrationNames.LegacySourceVersion} " +
            "database can be migrated; restore from a backup or migrate a supported source version.",
            SchemaMigrationErrorCodes.LegacySchemaUnrecognized);
    }

    private static string? FindFirstDifference(
        IReadOnlyList<string> expectedLines,
        IReadOnlyList<string> actualLines)
    {
        var sharedCount = Math.Min(expectedLines.Count, actualLines.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            if (!string.Equals(expectedLines[index], actualLines[index], StringComparison.Ordinal))
            {
                return $"expected '{expectedLines[index]}' but found '{actualLines[index]}'.";
            }
        }

        if (actualLines.Count < expectedLines.Count)
        {
            return $"'{expectedLines[sharedCount]}' is missing.";
        }

        if (actualLines.Count > expectedLines.Count)
        {
            return $"'{actualLines[sharedCount]}' is not part of that schema.";
        }

        return null;
    }
}
