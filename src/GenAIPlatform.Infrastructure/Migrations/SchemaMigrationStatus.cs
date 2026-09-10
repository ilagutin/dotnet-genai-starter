namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// A read-only comparison of the durable migration journal against the packaged migrations.
/// </summary>
public sealed record SchemaMigrationStatus(
    bool JournalPresent,
    string? JournalHead,
    string PackagedHead,
    IReadOnlyList<string> PendingVersions,
    IReadOnlyList<string> Mismatches)
{
    public bool IsUpToDate =>
        JournalPresent &&
        Mismatches.Count == 0 &&
        PendingVersions.Count == 0;
}
