using GenAIPlatform.Infrastructure.Migrations;

namespace GenAIPlatform.UnitTests;

public sealed class MigrationCatalogTests
{
    [Fact]
    public void Create_RejectsAGapInTheVersionSequence()
    {
        var exception = Assert.Throws<SchemaMigrationException>(() => MigrationCatalog.Create(
        [
            new MigrationScriptDefinition("0001", "first", "SELECT 1;"),
            new MigrationScriptDefinition("0003", "third", "SELECT 3;")
        ]));

        Assert.Equal(SchemaMigrationErrorCodes.CatalogInvalid, exception.ErrorCode);
        Assert.Contains("'0002'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsADuplicatedVersion()
    {
        var exception = Assert.Throws<SchemaMigrationException>(() => MigrationCatalog.Create(
        [
            new MigrationScriptDefinition("0001", "first", "SELECT 1;"),
            new MigrationScriptDefinition("0001", "first-again", "SELECT 1;")
        ]));

        Assert.Equal(SchemaMigrationErrorCodes.CatalogInvalid, exception.ErrorCode);
        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RejectsAnEmptyCatalogAndAnEmptyScript()
    {
        Assert.Throws<SchemaMigrationException>(() => MigrationCatalog.Create([]));
        Assert.Throws<SchemaMigrationException>(() => MigrationCatalog.Create(
        [
            new MigrationScriptDefinition("0001", "first", "   ")
        ]));
    }

    [Fact]
    public void Create_RejectsAVersionThatIsNotFourDigits()
    {
        var exception = Assert.Throws<SchemaMigrationException>(() => MigrationCatalog.Create(
        [
            new MigrationScriptDefinition("1", "first", "SELECT 1;")
        ]));

        Assert.Equal(SchemaMigrationErrorCodes.CatalogInvalid, exception.ErrorCode);
    }

    [Fact]
    public void Create_KeepsOrderAndChecksumsEachScript()
    {
        var catalog = MigrationCatalog.Create(
        [
            new MigrationScriptDefinition("0001", "first", "SELECT 1;"),
            new MigrationScriptDefinition("0002", "second", "SELECT 2;")
        ]);

        Assert.Equal(["0001", "0002"], catalog.Scripts.Select(static script => script.Version));
        Assert.Equal("0002", catalog.Head);
        Assert.Equal(MigrationChecksum.Compute("SELECT 1;"), catalog.Scripts[0].Checksum);
        Assert.Null(catalog.Find("0003"));
        Assert.Equal("second", catalog.Find("0002")!.Name);
    }

    [Fact]
    public void PackagedCatalog_MatchesTheManifestAndTheFrozenLegacyChecksums()
    {
        var catalog = EmbeddedMigrationCatalogFactory.Create();

        Assert.NotEmpty(catalog.Scripts);
        Assert.Equal(
            catalog.Scripts.Select(static script => script.Version).Order(StringComparer.Ordinal),
            catalog.Scripts.Select(static script => script.Version));

        foreach (var (version, frozenChecksum) in LegacyV031Baseline.Checksums)
        {
            Assert.Equal(frozenChecksum, catalog.Find(version)?.Checksum);
        }

        Assert.True(catalog.Scripts.Count >= LegacyV031Baseline.Checksums.Count);
    }

    /// <summary>
    /// The single-embedding-column migration is packaged after the frozen v0.3.1 baseline, so an
    /// adopted database applies it instead of inheriting it. Dropping it from the manifest, or
    /// slipping it in before 0006, would silently leave the duplicate column in place.
    /// </summary>
    [Fact]
    public void PackagedCatalog_ContainsTheSingleEmbeddingColumnMigrationAfterTheFrozenBaseline()
    {
        var catalog = EmbeddedMigrationCatalogFactory.Create();

        var script = catalog.Find("0007");

        Assert.NotNull(script);
        Assert.Equal("single-embedding-column", script.Name);
        Assert.Equal("0006", LegacyV031Baseline.AdoptedThroughVersion);
        Assert.DoesNotContain("0007", LegacyV031Baseline.Checksums.Keys);
        Assert.Equal("0007", catalog.Scripts[LegacyV031Baseline.Checksums.Count].Version);
        Assert.True(string.CompareOrdinal(catalog.Head, "0007") >= 0);
    }

    [Fact]
    public void PackagedFingerprint_DescribesOnlyMigrationOwnedObjects()
    {
        Assert.NotEmpty(LegacyV031Baseline.SchemaLines);
        Assert.All(LegacyV031Baseline.SchemaLines, static line =>
            Assert.DoesNotContain("schema_migration", line, StringComparison.Ordinal));
        Assert.Equal(
            LegacyV031Baseline.SchemaLines.Order(StringComparer.Ordinal),
            LegacyV031Baseline.SchemaLines);
    }
}
