namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// One disagreement between the durable journal and the packaged migrations, with the stable
/// error code the runner fails with and a sanitized operator-facing description.
/// </summary>
internal sealed record MigrationJournalMismatch(
    string Version,
    string ErrorCode,
    string Description);
