using GenAIPlatform.Infrastructure.Configuration;
using GenAIPlatform.Infrastructure.Migrations;
using GenAIPlatform.Infrastructure.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.IntegrationTests;

/// <summary>
/// Builds a schema migrator over one Testcontainers database. Tests can substitute the migration
/// catalog and the commit observer to inject faults that a packaged migration never has.
/// <para>
/// An <c>attemptRecordConnectionString</c> is used only for the failure-attempt record, which the
/// journal store writes on its own connection. Pointing it at an unreachable server fault-injects
/// a failing attempt-record write while every other journal operation still runs on the migration
/// connection.
/// </para>
/// </summary>
internal sealed class SchemaMigrationTestHost : IDisposable
{
    private readonly PostgresDataSourceProvider attemptRecordDataSourceProvider;

    public SchemaMigrationTestHost(
        string connectionString,
        IMigrationCatalog? catalog = null,
        MigrationCommitObserver? commitObserver = null,
        TimeSpan? lockTimeout = null,
        string? attemptRecordConnectionString = null)
    {
        DataSourceProvider = CreateDataSourceProvider(connectionString);
        attemptRecordDataSourceProvider = attemptRecordConnectionString is null
            ? DataSourceProvider
            : CreateDataSourceProvider(attemptRecordConnectionString);
        Catalog = catalog ?? EmbeddedMigrationCatalogFactory.Create();
        JournalStore = new MigrationJournalStore(attemptRecordDataSourceProvider);
        JournalHeadReader = new MigrationJournalHeadReader(Catalog, JournalStore);
        Migrator = new PostgresSchemaMigrator(
            DataSourceProvider,
            Catalog,
            JournalStore,
            new PostgresAdvisoryLock(),
            new LegacySchemaAdopter(new SchemaFingerprintReader()),
            new SchemaMigrationErrorMapper(),
            commitObserver ?? new MigrationCommitObserver(),
            Options.Create(new MigrationOptions
            {
                LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30)
            }),
            TimeProvider.System);
    }

    public PostgresDataSourceProvider DataSourceProvider { get; }

    public IMigrationCatalog Catalog { get; }

    public MigrationJournalStore JournalStore { get; }

    public MigrationJournalHeadReader JournalHeadReader { get; }

    public PostgresSchemaMigrator Migrator { get; }

    /// <summary>
    /// Leaves behind exactly what an interrupted first run can leave: the journal tables with no
    /// rows in them. The DDL is the production one, so the simulation cannot drift.
    /// </summary>
    public async Task CreateEmptyJournalAsync(CancellationToken cancellationToken)
    {
        await using var connection = await DataSourceProvider.OpenConnectionAsync(cancellationToken);
        await JournalStore.EnsureJournalAsync(connection, cancellationToken);
    }

    public void Dispose()
    {
        if (!ReferenceEquals(attemptRecordDataSourceProvider, DataSourceProvider))
        {
            attemptRecordDataSourceProvider.Dispose();
        }

        DataSourceProvider.Dispose();
    }

    private static PostgresDataSourceProvider CreateDataSourceProvider(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = connectionString
            })
            .Build();

        return new PostgresDataSourceProvider(
            configuration,
            Options.Create(new PostgresOptions()));
    }
}
