using GenAIPlatform.Infrastructure.Postgres;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Normalizes Npgsql failures into sanitized <see cref="SchemaMigrationException"/> contracts.
/// Descriptions carry the failure shape and the PostgreSQL SQLSTATE only, never the migration
/// SQL, the raw provider message, credentials or a connection string.
/// </summary>
internal sealed class SchemaMigrationErrorMapper
{
    public SchemaMigrationException ApplyFailed(
        string version,
        MigrationAttemptOutcome outcome,
        Exception exception,
        string? attemptRecordFailure)
    {
        return Interrupted(
            $"Schema migration '{version}' failed ({Describe(exception)}).",
            SchemaMigrationErrorCodes.ApplyFailed,
            outcome,
            exception,
            attemptRecordFailure);
    }

    /// <summary>
    /// A run the operator stopped while a migration was in flight. It is reported as a
    /// cancellation rather than a failure, because nothing about the migration itself is wrong,
    /// but the effect on the journal is described exactly as for a failure.
    /// </summary>
    public SchemaMigrationException ApplyCanceled(
        string version,
        MigrationAttemptOutcome outcome,
        Exception exception,
        string? attemptRecordFailure)
    {
        return Interrupted(
            $"Schema migration '{version}' was canceled.",
            SchemaMigrationErrorCodes.Canceled,
            outcome,
            exception,
            attemptRecordFailure);
    }

    public SchemaMigrationException Canceled(Exception exception)
    {
        return new SchemaMigrationException(
            "Schema migration was canceled before a migration was applied; the migration journal " +
            "was not changed.",
            SchemaMigrationErrorCodes.Canceled,
            exception);
    }

    /// <summary>
    /// The connection string the migration host needs is missing or unusable. The originating
    /// message names the configuration key only and never carries the connection string itself.
    /// </summary>
    public SchemaMigrationException NotConfigured(PostgresConnectionConfigurationException exception)
    {
        return new SchemaMigrationException(
            $"{exception.Message} The migration host needs it to reach PostgreSQL; nothing was read " +
            "or changed.",
            SchemaMigrationErrorCodes.NotConfigured,
            exception);
    }

    public SchemaMigrationException StoreFailed(Exception exception)
    {
        return new SchemaMigrationException(
            $"The schema migration journal could not be read or created ({Describe(exception)}). " +
            $"The migration role needs DDL rights on schema {MigrationNames.SchemaName}.",
            SchemaMigrationErrorCodes.StoreFailed,
            exception);
    }

    public SchemaMigrationException Unavailable(Exception exception)
    {
        return new SchemaMigrationException(
            $"The schema migration store is unavailable ({Describe(exception)}).",
            SchemaMigrationErrorCodes.Unavailable,
            exception);
    }

    private static SchemaMigrationException Interrupted(
        string summary,
        string errorCode,
        MigrationAttemptOutcome outcome,
        Exception exception,
        string? attemptRecordFailure)
    {
        var effect = outcome == MigrationAttemptOutcome.Unknown
            ? "The commit had already been sent, so this version may or may not have been applied; " +
              "rerun the migration command to reconcile the journal."
            : "The transaction was rolled back and no journal entry was written.";
        var message = $"{summary} {effect}";
        if (attemptRecordFailure is not null)
        {
            message +=
                $" The failure record could not be written either ({attemptRecordFailure}), " +
                $"so {MigrationNames.AttemptTable} does not describe this failure.";
        }

        return new SchemaMigrationException(message, errorCode, exception);
    }

    /// <summary>
    /// Describes a failure without echoing anything the database wrote. The one exception is
    /// <see cref="MigrationNames.PreconditionSqlState"/>: that SQLSTATE can only come from a
    /// <c>RAISE</c> inside a packaged migration script, so its message text is authored by this
    /// repository and states counts and a repair instruction, never row content. Without it a
    /// data precondition would fail with a bare error code and leave the operator no way to act.
    /// </summary>
    public static string Describe(Exception exception)
    {
        return exception switch
        {
            PostgresException postgres when string.Equals(
                postgres.SqlState,
                MigrationNames.PreconditionSqlState,
                StringComparison.Ordinal) =>
                $"PostgreSQL error {postgres.SqlState}: {SingleLine(postgres.MessageText)}",
            PostgresException postgres => $"PostgreSQL error {postgres.SqlState}",
            NpgsqlException => "PostgreSQL connection error",
            TimeoutException => "PostgreSQL command timeout",
            OperationCanceledException => "cancellation requested",
            _ => exception.GetType().Name
        };
    }

    /// <summary>
    /// Keeps a surfaced precondition message on the single line the migration CLI prints.
    /// </summary>
    private static string SingleLine(string message)
    {
        return string.Join(' ', message.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
