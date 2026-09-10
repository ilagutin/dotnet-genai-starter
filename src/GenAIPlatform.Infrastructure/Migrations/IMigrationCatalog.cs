namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// The ordered set of migrations a runner may apply. Implementations fail closed: an invalid
/// catalog throws instead of exposing a partial migration set.
/// </summary>
internal interface IMigrationCatalog
{
    IReadOnlyList<MigrationScript> Scripts { get; }

    string Head { get; }

    MigrationScript? Find(string version);
}
