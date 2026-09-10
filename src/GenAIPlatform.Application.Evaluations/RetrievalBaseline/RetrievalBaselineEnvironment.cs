namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// The runtime a baseline run measured on. Values are version strings only, never
/// connection strings, hosts or credentials.
/// </summary>
public sealed record RetrievalBaselineEnvironment(
    string OperatingSystem,
    string Runtime,
    string PostgresVersion,
    string? PgvectorVersion);
