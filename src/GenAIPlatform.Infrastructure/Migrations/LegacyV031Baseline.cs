namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// The frozen v0.3.1 baseline: the exact schema fingerprint the released initialization scripts
/// produce, and the checksums those scripts hash to. Both are packaged as embedded resources so
/// adoption does not depend on the checkout directory.
/// </summary>
internal static class LegacyV031Baseline
{
    public const string SchemaResourceName =
        EmbeddedMigrationResources.FingerprintPrefix + "legacy-v0.3.1-schema.txt";

    public const string ChecksumResourceName =
        EmbeddedMigrationResources.FingerprintPrefix + "legacy-v0.3.1-checksums.txt";

    private static readonly Lazy<IReadOnlyList<string>> LazySchemaLines =
        new(() => EmbeddedMigrationResources.ReadLines(SchemaResourceName));

    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyChecksums =
        new(ReadChecksums);

    /// <summary>
    /// The last packaged version an adopted v0.3.1 database is journalled through.
    /// </summary>
    public static string AdoptedThroughVersion => Checksums.Keys.Order(StringComparer.Ordinal).Last();

    public static IReadOnlyList<string> SchemaLines => LazySchemaLines.Value;

    public static IReadOnlyDictionary<string, string> Checksums => LazyChecksums.Value;

    private static IReadOnlyDictionary<string, string> ReadChecksums()
    {
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in EmbeddedMigrationResources.ReadLines(ChecksumResourceName))
        {
            var separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                throw MigrationCatalog.Invalid(
                    $"The frozen {MigrationNames.LegacySourceVersion} checksum resource is malformed.");
            }

            checksums[line[..separatorIndex]] = line[(separatorIndex + 1)..];
        }

        if (checksums.Count == 0)
        {
            throw MigrationCatalog.Invalid(
                $"The frozen {MigrationNames.LegacySourceVersion} checksum resource lists no migrations.");
        }

        return checksums;
    }
}
