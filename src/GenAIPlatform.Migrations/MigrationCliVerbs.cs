namespace GenAIPlatform.Migrations;

/// <summary>
/// The verbs the migration CLI accepts and the usage text shown for anything else.
/// </summary>
public static class MigrationCliVerbs
{
    public const string Migrate = "migrate";
    public const string Status = "status";

    public const string UsageText = """
        Usage: dotnet run --project src/GenAIPlatform.Migrations -- <verb>

        Verbs:
          migrate   Apply pending schema migrations (default).
          status    Report the journal against the packaged migrations without changing anything.

        Exit codes:
          0   Schema is up to date, or migrations were applied.
          1   Schema is behind (status), or the migration failed.
          2   Usage or configuration error.

        Stop API, Worker, MCP and CLI hosts before migrating, and take a database backup first.
        """;

    public static bool IsKnown(string? verb)
    {
        return string.Equals(verb, Migrate, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(verb, Status, StringComparison.OrdinalIgnoreCase);
    }
}
