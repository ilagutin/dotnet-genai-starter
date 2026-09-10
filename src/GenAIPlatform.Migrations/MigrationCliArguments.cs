namespace GenAIPlatform.Migrations;

/// <summary>
/// The parsed command line: the verb this run performs and the remaining arguments that belong
/// to the configuration binder.
/// </summary>
public sealed record MigrationCliArguments(string Verb, string[] ConfigurationArgs);
