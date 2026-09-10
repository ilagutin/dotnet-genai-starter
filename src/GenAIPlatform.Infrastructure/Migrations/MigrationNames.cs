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

    public const string MigrateCommand =
        "dotnet run --project src/GenAIPlatform.Migrations -- migrate";

    public const string StaleSchemaHint =
        "PostgreSQL schema is not up to date: the " + JournalTable +
        " journal is missing or behind the packaged migrations. Run `" + MigrateCommand + "`.";
}
