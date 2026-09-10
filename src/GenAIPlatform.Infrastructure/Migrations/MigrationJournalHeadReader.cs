using Npgsql;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Readiness view of the migration journal. Hosts do not migrate; they refuse to look usable
/// while the journal is missing or behind the migrations packaged in this build.
/// </summary>
internal sealed class MigrationJournalHeadReader(
    IMigrationCatalog catalog,
    MigrationJournalStore journalStore)
{
    public async Task<bool> IsJournalCurrentAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var head = await journalStore.ReadHeadAsync(connection, cancellationToken);

        return string.Equals(head, catalog.Head, StringComparison.Ordinal);
    }
}
