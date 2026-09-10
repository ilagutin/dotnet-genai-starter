namespace GenAIPlatform.Migrations;

/// <summary>
/// Either parsed arguments or a usage error; never both.
/// </summary>
public sealed record MigrationCliParseResult(MigrationCliArguments? Arguments, string? Error);
