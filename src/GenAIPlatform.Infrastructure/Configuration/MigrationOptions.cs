namespace GenAIPlatform.Infrastructure.Configuration;

public sealed class MigrationOptions
{
    public const string SectionName = "GenAIPlatform:Migrations";

    /// <summary>
    /// How long a runner waits for the migration advisory lock before failing without changing
    /// the journal.
    /// </summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Command timeout for one migration script. Schema changes on a populated database can take
    /// longer than the Npgsql default.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 300;

    public bool IsValid()
    {
        return LockTimeout > TimeSpan.Zero && CommandTimeoutSeconds > 0;
    }
}
