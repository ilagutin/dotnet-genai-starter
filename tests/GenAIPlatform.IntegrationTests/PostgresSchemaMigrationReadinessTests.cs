using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Infrastructure;
using GenAIPlatform.Infrastructure.Documents.Postgres.IndexingJobs;
using GenAIPlatform.Infrastructure.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresSchemaMigrationReadinessTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task FrozenLegacyScripts_ProduceTheEmbeddedV031Fingerprint()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyLegacyV031SchemaAsync(connectionString);

            var actualLines =
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString);

            Assert.Equal(LegacyV031Baseline.SchemaLines, actualLines);
        }
        finally
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task RetrievalReadiness_FailsWhileTheJournalIsBehindAndPassesWhenItIsCurrent()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyLegacyV031SchemaAsync(connectionString);

            using var scope = CreateInfrastructureScope(connectionString);
            var vectorSearchStore = scope.ServiceProvider.GetRequiredService<IRagVectorSearchStore>();

            var exception = await Assert.ThrowsAsync<RagVectorSearchException>(() =>
                vectorSearchStore.CheckReadinessAsync(TestContext.Current.CancellationToken));

            Assert.Equal("retrieval_schema_error", exception.ErrorCode);
            Assert.Contains(
                MigrationNames.MigrateCommand,
                exception.Message,
                StringComparison.Ordinal);

            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);

            await vectorSearchStore.CheckReadinessAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task IndexingReadiness_FailsWhileTheJournalIsBehindAndPassesWhenItIsCurrent()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyLegacyV031SchemaAsync(connectionString);

            using var scope = CreateInfrastructureScope(connectionString);
            var readiness = scope.ServiceProvider
                .GetRequiredService<PostgresIndexingSchemaReadiness>();

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                var exception = await Assert
                    .ThrowsAsync<DocumentIndexingSchemaNotReadyException>(() =>
                        readiness.EnsureReadyAsync(
                            connection,
                            TestContext.Current.CancellationToken));

                Assert.Contains(
                    MigrationNames.MigrateCommand,
                    exception.Message,
                    StringComparison.Ordinal);
            }

            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await readiness.EnsureReadyAsync(connection, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

    private static IServiceScope CreateInfrastructureScope(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = connectionString
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        var serviceProvider = services.BuildServiceProvider();

        return serviceProvider.CreateScope();
    }
}
