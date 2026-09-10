using GenAIPlatform.Infrastructure.Migrations;

namespace GenAIPlatform.IntegrationTests;

/// <summary>
/// Simulates a lost commit acknowledgement: the commit reached PostgreSQL, but the runner never
/// learns that it succeeded.
/// </summary>
internal sealed class ThrowingMigrationCommitObserver(string versionToFail) : MigrationCommitObserver
{
    public override Task AfterCommitAsync(string version, CancellationToken cancellationToken)
    {
        if (string.Equals(version, versionToFail, StringComparison.Ordinal))
        {
            throw new TimeoutException("Simulated loss of the commit acknowledgement.");
        }

        return Task.CompletedTask;
    }
}
