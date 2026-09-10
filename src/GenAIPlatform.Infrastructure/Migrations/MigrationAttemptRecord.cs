namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// A sanitized failure record. <see cref="ErrorSummary"/> carries the failure shape only:
/// never the migration SQL, the raw provider message, credentials or a connection string.
/// </summary>
internal sealed record MigrationAttemptRecord(
    string Version,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    MigrationAttemptOutcome Outcome,
    string ErrorType,
    string ErrorSummary);
