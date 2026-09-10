namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Builds the packaged migration catalog from the embedded manifest. The manifest and the
/// embedded SQL resources must agree exactly, so a forgotten manifest line or an orphaned
/// resource fails before any SQL runs instead of silently skipping a migration.
/// </summary>
internal static class EmbeddedMigrationCatalogFactory
{
    public static MigrationCatalog Create()
    {
        var fileNames = EmbeddedMigrationResources.ReadLines(
            EmbeddedMigrationResources.ManifestResourceName);
        if (fileNames.Count == 0)
        {
            throw MigrationCatalog.Invalid("The embedded schema migration manifest lists no migrations.");
        }

        var listedResourceNames = fileNames
            .Select(static fileName => EmbeddedMigrationResources.SqlPrefix + fileName)
            .ToArray();
        var embeddedResourceNames = EmbeddedMigrationResources.SqlResourceNames();
        var orphaned = embeddedResourceNames
            .Except(listedResourceNames, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (orphaned.Length > 0)
        {
            throw MigrationCatalog.Invalid(
                $"Embedded migration resource '{orphaned[0]}' is not listed in the migration manifest.");
        }

        return MigrationCatalog.Create(
            [.. fileNames.Select(CreateDefinition)]);
    }

    private static MigrationScriptDefinition CreateDefinition(string fileName)
    {
        var separatorIndex = fileName.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex <= 0 || !fileName.EndsWith(".sql", StringComparison.Ordinal))
        {
            throw MigrationCatalog.Invalid(
                $"Migration manifest entry '{fileName}' is not named '<version>-<name>.sql'.");
        }

        var version = fileName[..separatorIndex];
        var name = fileName[(separatorIndex + 1)..^".sql".Length];
        var sql = EmbeddedMigrationResources.ReadText(EmbeddedMigrationResources.SqlPrefix + fileName);

        return new MigrationScriptDefinition(version, name, sql);
    }
}
