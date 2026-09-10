using System.Text;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Reads the migration SQL packaged into the Infrastructure assembly, so the authoritative
/// upgrade SQL travels with a published host instead of with the checkout directory.
/// </summary>
internal static class EmbeddedMigrationResources
{
    public const string SqlPrefix = "GenAIPlatform.Infrastructure.Migrations.Sql.";
    public const string ManifestResourceName = SqlPrefix + "migrations.manifest";
    public const string FingerprintPrefix = "GenAIPlatform.Infrastructure.Migrations.Fingerprints.";

    public static string ReadText(string resourceName)
    {
        var assembly = typeof(Setup).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw MigrationCatalog.Invalid(
                $"Embedded migration resource '{resourceName}' is missing from the Infrastructure assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<string> ReadLines(string resourceName)
    {
        return MigrationChecksum
            .Canonicalize(ReadText(resourceName))
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }

    public static IReadOnlyList<string> SqlResourceNames()
    {
        return typeof(Setup).Assembly
            .GetManifestResourceNames()
            .Where(static name => name.StartsWith(SqlPrefix, StringComparison.Ordinal))
            .Where(static name => name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
