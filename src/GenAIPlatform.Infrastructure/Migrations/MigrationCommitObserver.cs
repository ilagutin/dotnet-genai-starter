namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Seam for the one moment a migration run cannot observe by itself: the window after a commit
/// has been sent and before the runner learns whether it landed. The default implementation does
/// nothing; fault-injection tests override it to simulate a lost commit acknowledgement.
/// </summary>
internal class MigrationCommitObserver
{
    public virtual Task AfterCommitAsync(string version, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
