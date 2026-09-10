namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Applies the packaged, ordered schema migrations to the configured PostgreSQL database.
/// Hosts never migrate on startup; an operator runs the migration host explicitly.
/// </summary>
public interface ISchemaMigrator
{
    Task<SchemaMigrationResult> MigrateAsync(CancellationToken cancellationToken);

    Task<SchemaMigrationStatus> GetStatusAsync(CancellationToken cancellationToken);
}
