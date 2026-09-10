namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// How a failed migration attempt ended. <see cref="RolledBack"/> means the transaction was
/// rolled back and nothing was applied. <see cref="Unknown"/> means the commit was already sent
/// when the failure surfaced, so the transaction may or may not have landed.
/// </summary>
internal enum MigrationAttemptOutcome
{
    RolledBack,
    Unknown
}
