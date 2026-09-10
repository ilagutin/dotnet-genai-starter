using System.Globalization;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// A validated migration catalog. Versions must be four-digit, contiguous from 0001 and unique,
/// so a missing or duplicated migration resource fails before any SQL runs.
/// </summary>
internal sealed class MigrationCatalog : IMigrationCatalog
{
    private readonly Dictionary<string, MigrationScript> scriptsByVersion;

    private MigrationCatalog(IReadOnlyList<MigrationScript> scripts)
    {
        Scripts = scripts;
        Head = scripts[^1].Version;
        scriptsByVersion = scripts.ToDictionary(
            static script => script.Version,
            StringComparer.Ordinal);
    }

    public IReadOnlyList<MigrationScript> Scripts { get; }

    public string Head { get; }

    public static MigrationCatalog Create(IReadOnlyList<MigrationScriptDefinition> definitions)
    {
        if (definitions.Count == 0)
        {
            throw Invalid("The schema migration catalog is empty.");
        }

        var scripts = new List<MigrationScript>(definitions.Count);
        var seenVersions = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            if (!IsVersionWellFormed(definition.Version))
            {
                throw Invalid(
                    $"Schema migration version '{definition.Version}' is not a four-digit version.");
            }

            if (!seenVersions.Add(definition.Version))
            {
                throw Invalid(
                    $"Schema migration version '{definition.Version}' is declared more than once.");
            }

            var expectedVersion = (index + 1).ToString("D4", CultureInfo.InvariantCulture);
            if (!string.Equals(definition.Version, expectedVersion, StringComparison.Ordinal))
            {
                throw Invalid(
                    "Schema migration versions must be contiguous and ordered; expected " +
                    $"'{expectedVersion}' but found '{definition.Version}'.");
            }

            if (string.IsNullOrWhiteSpace(definition.Sql))
            {
                throw Invalid($"Schema migration '{definition.Version}' has no SQL content.");
            }

            scripts.Add(new MigrationScript(
                definition.Version,
                definition.Name,
                definition.Sql,
                MigrationChecksum.Compute(definition.Sql)));
        }

        return new MigrationCatalog(scripts);
    }

    public MigrationScript? Find(string version)
    {
        return scriptsByVersion.GetValueOrDefault(version);
    }

    public static SchemaMigrationException Invalid(string message)
    {
        return new SchemaMigrationException(message, SchemaMigrationErrorCodes.CatalogInvalid);
    }

    private static bool IsVersionWellFormed(string version)
    {
        return version.Length == 4 && version.All(char.IsAsciiDigit);
    }
}
