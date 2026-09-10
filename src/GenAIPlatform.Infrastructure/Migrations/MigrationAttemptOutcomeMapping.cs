namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Persistence spelling of <see cref="MigrationAttemptOutcome"/>. Undefined values fail closed.
/// </summary>
internal static class MigrationAttemptOutcomeMapping
{
    public const string RolledBackValue = "rolled-back";
    public const string UnknownValue = "unknown";

    public static string ToPersistenceValue(MigrationAttemptOutcome outcome)
    {
        return outcome switch
        {
            MigrationAttemptOutcome.RolledBack => RolledBackValue,
            MigrationAttemptOutcome.Unknown => UnknownValue,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }
}
