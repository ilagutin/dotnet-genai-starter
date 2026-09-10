using GenAIPlatform.Infrastructure.Migrations;

namespace GenAIPlatform.UnitTests;

/// <summary>
/// Returns a scripted migration result or failure, so CLI output and exit codes can be tested
/// without a database.
/// </summary>
internal sealed class FakeSchemaMigrator : ISchemaMigrator
{
    public SchemaMigrationResult Result { get; init; } =
        new([], AdoptedLegacySchema: false, JournalHead: "0006");

    public SchemaMigrationStatus Status { get; init; } =
        new(JournalPresent: true, JournalHead: "0006", PackagedHead: "0006", [], []);

    public SchemaMigrationException? Failure { get; init; }

    public Task<SchemaMigrationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Failure is null
            ? Task.FromResult(Result)
            : Task.FromException<SchemaMigrationResult>(Failure);
    }

    public Task<SchemaMigrationStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Failure is null
            ? Task.FromResult(Status)
            : Task.FromException<SchemaMigrationStatus>(Failure);
    }
}
