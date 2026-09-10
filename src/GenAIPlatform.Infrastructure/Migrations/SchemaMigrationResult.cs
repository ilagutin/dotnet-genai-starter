namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// The outcome of one migration run. <paramref name="AppliedVersions"/> is empty when the
/// database was already up to date.
/// </summary>
public sealed record SchemaMigrationResult(
    IReadOnlyList<string> AppliedVersions,
    bool AdoptedLegacySchema,
    string? JournalHead);
