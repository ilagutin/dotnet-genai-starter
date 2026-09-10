using GenAIPlatform.Infrastructure.Configuration;
using GenAIPlatform.Infrastructure.Migrations;
using GenAIPlatform.Infrastructure.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

/// <summary>
/// The connection-configuration failure is normalized at the <see cref="ISchemaMigrator"/> port
/// boundary, so no caller has to recognize a provider exception type or match a message prefix.
/// </summary>
public sealed class SchemaMigratorConfigurationTests
{
    [Fact]
    public async Task MigrateAsync_WithoutAConnectionString_FailsWithTheNotConfiguredCode()
    {
        using var dataSourceProvider = CreateProviderWithoutAConnectionString();
        var migrator = CreateMigrator(dataSourceProvider);

        var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
            migrator.MigrateAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SchemaMigrationErrorCodes.NotConfigured, exception.ErrorCode);
        Assert.Contains("GenAIPlatform", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not configured", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatusAsync_WithoutAConnectionString_FailsWithTheNotConfiguredCode()
    {
        using var dataSourceProvider = CreateProviderWithoutAConnectionString();
        var migrator = CreateMigrator(dataSourceProvider);

        var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
            migrator.GetStatusAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SchemaMigrationErrorCodes.NotConfigured, exception.ErrorCode);
    }

    private static PostgresDataSourceProvider CreateProviderWithoutAConnectionString()
    {
        return new PostgresDataSourceProvider(
            new ConfigurationBuilder().Build(),
            Options.Create(new PostgresOptions()));
    }

    private static PostgresSchemaMigrator CreateMigrator(
        PostgresDataSourceProvider dataSourceProvider)
    {
        return new PostgresSchemaMigrator(
            dataSourceProvider,
            EmbeddedMigrationCatalogFactory.Create(),
            new MigrationJournalStore(dataSourceProvider),
            new PostgresAdvisoryLock(),
            new LegacySchemaAdopter(new SchemaFingerprintReader()),
            new SchemaMigrationErrorMapper(),
            new MigrationCommitObserver(),
            Options.Create(new MigrationOptions()),
            TimeProvider.System);
    }
}
