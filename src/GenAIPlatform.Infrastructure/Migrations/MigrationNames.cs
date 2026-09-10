namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Checkpoint names for the migration runner. They are referenced from SQL, readiness messages,
/// documentation and tests so a rename stays a single edit.
/// </summary>
internal static class MigrationNames
{
    public const string SchemaName = "genai";
    public const string JournalTable = "genai.schema_migrations";
    public const string AttemptTable = "genai.schema_migration_attempts";
    public const string AdvisoryLockKey = "genai.schema_migrations";
    public const string LegacySourceVersion = "v0.3.1";

    /// <summary>
    /// The SQLSTATE a packaged migration raises when a data precondition fails. It is in the
    /// user-defined range and is used by no PostgreSQL built-in, so a failure carrying it can
    /// only come from a <c>RAISE</c> written into one of the packaged scripts. Its message text
    /// is therefore authored, sanitized text that the runner may show to an operator; every
    /// other SQLSTATE keeps reporting the code alone, because a provider message can quote row
    /// values.
    /// </summary>
    public const string PreconditionSqlState = "GN001";

    public const string MigrateCommand =
        "dotnet run --project src/GenAIPlatform.Migrations -- migrate";

    public const string StaleSchemaHint =
        "PostgreSQL schema is not up to date: the " + JournalTable +
        " journal is missing or behind the packaged migrations. Run `" + MigrateCommand + "`.";
}
