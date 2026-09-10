namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Compares the durable journal with the packaged catalog. A changed applied script or a
/// journalled version the catalog does not know fails closed: the runner never rewrites or
/// ignores an existing journal row.
/// </summary>
internal static class MigrationJournalValidator
{
    public static IReadOnlyList<MigrationJournalMismatch> FindMismatches(
        IMigrationCatalog catalog,
        IReadOnlyList<MigrationJournalEntry> entries)
    {
        var mismatches = new List<MigrationJournalMismatch>();
        foreach (var entry in entries)
        {
            var script = catalog.Find(entry.Version);
            if (script is null)
            {
                mismatches.Add(new MigrationJournalMismatch(
                    entry.Version,
                    SchemaMigrationErrorCodes.UnknownVersion,
                    $"Migration '{entry.Version}' is recorded in {MigrationNames.JournalTable} but is not " +
                    "part of the packaged migrations. This database was migrated by a newer build."));
                continue;
            }

            if (!string.Equals(script.Checksum, entry.Checksum, StringComparison.Ordinal))
            {
                mismatches.Add(new MigrationJournalMismatch(
                    entry.Version,
                    SchemaMigrationErrorCodes.ChecksumMismatch,
                    $"Migration '{entry.Version}' was applied from a different script than the packaged " +
                    "one. Applied migration SQL is immutable; add a new migration instead of editing it."));
            }
        }

        return mismatches;
    }

    public static IReadOnlyList<string> FindPendingVersions(
        IMigrationCatalog catalog,
        IReadOnlyList<MigrationJournalEntry> entries)
    {
        var applied = entries
            .Select(static entry => entry.Version)
            .ToHashSet(StringComparer.Ordinal);

        return
        [
            .. catalog.Scripts
                .Where(script => !applied.Contains(script.Version))
                .Select(static script => script.Version)
        ];
    }

    public static void EnsureConsistent(
        IMigrationCatalog catalog,
        IReadOnlyList<MigrationJournalEntry> entries)
    {
        var mismatches = FindMismatches(catalog, entries);
        if (mismatches.Count == 0)
        {
            return;
        }

        throw new SchemaMigrationException(mismatches[0].Description, mismatches[0].ErrorCode);
    }
}
