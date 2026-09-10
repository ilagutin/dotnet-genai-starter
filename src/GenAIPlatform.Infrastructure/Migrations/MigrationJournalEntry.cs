namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// One durable journal row: the version that was applied and the checksum of the SQL that ran.
/// </summary>
internal sealed record MigrationJournalEntry(
    string Version,
    string Name,
    string Checksum,
    bool Adopted);
