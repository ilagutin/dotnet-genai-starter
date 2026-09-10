namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// What an un-journalled database is. Anything that is neither empty nor an exact frozen
/// baseline is a failure, not a third decision: the runner never guesses a baseline.
/// </summary>
internal enum LegacySchemaDecision
{
    Fresh,
    AdoptLegacyBaseline
}
