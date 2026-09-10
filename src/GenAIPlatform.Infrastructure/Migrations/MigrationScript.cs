namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// One validated migration with the checksum recorded in the journal when it is applied.
/// </summary>
internal sealed record MigrationScript(string Version, string Name, string Sql, string Checksum);
