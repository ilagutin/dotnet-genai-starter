namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// One migration before catalog validation: the ordered version, its human-readable name and
/// the raw SQL text as it was read from its source.
/// </summary>
internal sealed record MigrationScriptDefinition(string Version, string Name, string Sql);
