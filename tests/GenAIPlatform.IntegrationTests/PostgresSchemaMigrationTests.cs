using GenAIPlatform.Infrastructure.Migrations;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresSchemaMigrationTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task Migrate_OnEmptyDatabase_AppliesEveryPackagedMigrationOnce()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.EnableVectorExtensionAsync(connectionString);

            using var host = new SchemaMigrationTestHost(connectionString);
            var result = await host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.False(result.AdoptedLegacySchema);
            Assert.Equal(
                host.Catalog.Scripts.Select(static script => script.Version),
                result.AppliedVersions);
            Assert.Equal(host.Catalog.Head, result.JournalHead);

            var journal = await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);
            Assert.Equal(host.Catalog.Scripts.Count, journal.Count);
            Assert.All(journal, static entry => Assert.False(entry.Adopted));
            Assert.Equal(
                host.Catalog.Scripts.Select(static script => script.Checksum),
                journal.Select(static entry => entry.Checksum));
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WhenRepeated_IsANoOp()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.EnableVectorExtensionAsync(connectionString);

            using var host = new SchemaMigrationTestHost(connectionString);
            await host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);
            var journalAfterFirstRun =
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);

            var repeat = await host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.Empty(repeat.AppliedVersions);
            Assert.False(repeat.AdoptedLegacySchema);
            Assert.Equal(host.Catalog.Head, repeat.JournalHead);
            Assert.Equal(
                journalAfterFirstRun,
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_OnPopulatedFrozenLegacyDatabase_AdoptsAndConvergesWithFreshDatabase()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
            var freshSnapshot =
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString);

            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyLegacyV031SchemaAsync(connectionString);
            await LegacyV031SeedData.SeedAsync(connectionString);
            var seededRowCounts = await LegacyV031SeedData.ReadRowCountsAsync(connectionString);

            using var host = new SchemaMigrationTestHost(connectionString);
            var result = await host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.True(result.AdoptedLegacySchema);
            Assert.Empty(result.AppliedVersions);
            Assert.Equal(host.Catalog.Head, result.JournalHead);
            Assert.Equal(
                freshSnapshot,
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString));
            Assert.Equal(
                seededRowCounts,
                await LegacyV031SeedData.ReadRowCountsAsync(connectionString));

            var sampleValues = await LegacyV031SeedData.ReadSampleValuesAsync(connectionString);
            Assert.Equal("Legacy Notes", sampleValues["documents.title"]);
            Assert.Equal(new string('3', 64), sampleValues["document_chunks.text_hash"]);
            Assert.Equal("Succeeded", sampleValues["evaluation_runs.status"]);

            var journal = await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);
            Assert.All(journal, static entry => Assert.True(entry.Adopted));
            Assert.Equal(
                LegacyV031Baseline.Checksums.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => pair.Value),
                journal.Select(static entry => entry.Checksum));
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_OnPartialLegacyDatabase_FailsAndChangesNothing()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
                connectionString,
                "001-enable-pgvector.sql",
                "002-document-ingestion.sql",
                "003-pgvector-retrieval.sql");
            var snapshotBefore =
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString);

            using var host = new SchemaMigrationTestHost(connectionString);
            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                host.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            Assert.Equal(SchemaMigrationErrorCodes.LegacySchemaUnrecognized, exception.ErrorCode);
            Assert.Contains("v0.3.1", exception.Message, StringComparison.Ordinal);
            Assert.False(await SchemaMigrationTestSupport.TableExistsAsync(
                connectionString,
                "schema_migrations"));
            Assert.Equal(
                snapshotBefore,
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString));
        }
        finally
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WithAnEmptyJournalTableOnTheFrozenLegacySchema_AdoptsWithFrozenChecksums()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyLegacyV031SchemaAsync(connectionString);

            using var host = new SchemaMigrationTestHost(connectionString);
            await host.CreateEmptyJournalAsync(TestContext.Current.CancellationToken);
            Assert.True(await SchemaMigrationTestSupport.TableExistsAsync(
                connectionString,
                "schema_migrations"));
            Assert.Empty(await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));

            var result = await host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.True(result.AdoptedLegacySchema);
            Assert.Empty(result.AppliedVersions);

            var journal = await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);
            Assert.All(journal, static entry => Assert.True(entry.Adopted));
            Assert.Equal(
                LegacyV031Baseline.Checksums.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => pair.Value),
                journal.Select(static entry => entry.Checksum));
        }
        finally
        {
            await PostgresSchemaTestHelper.RebuildSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_AfterAnInterruptedRunLeftAnEmptyJournal_AdoptsInsteadOfReplaying()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyLegacyV031SchemaAsync(connectionString);
            await LegacyV031SeedData.SeedAsync(connectionString);
            var seededRowCounts = await LegacyV031SeedData.ReadRowCountsAsync(connectionString);
            var snapshotBefore =
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString);

            using var host = new SchemaMigrationTestHost(connectionString);
            await host.CreateEmptyJournalAsync(TestContext.Current.CancellationToken);

            var result = await host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.True(result.AdoptedLegacySchema);
            Assert.Empty(result.AppliedVersions);
            Assert.Equal(host.Catalog.Head, result.JournalHead);
            Assert.Equal(
                snapshotBefore,
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString));
            Assert.Equal(
                seededRowCounts,
                await LegacyV031SeedData.ReadRowCountsAsync(connectionString));
            Assert.All(
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString),
                static entry => Assert.True(entry.Adopted));
        }
        finally
        {
            await PostgresSchemaTestHelper.RebuildSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WithAnEmptyJournalTableOnAPartialSchema_FailsClosed()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.ApplyInitScriptsAsync(
                connectionString,
                "001-enable-pgvector.sql",
                "002-document-ingestion.sql",
                "003-pgvector-retrieval.sql");

            using var host = new SchemaMigrationTestHost(connectionString);
            await host.CreateEmptyJournalAsync(TestContext.Current.CancellationToken);
            var snapshotBefore =
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString);

            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                host.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            Assert.Equal(SchemaMigrationErrorCodes.LegacySchemaUnrecognized, exception.ErrorCode);
            Assert.Empty(await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));
            Assert.Equal(
                snapshotBefore,
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString));
        }
        finally
        {
            await PostgresSchemaTestHelper.RebuildSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WithAnEmptyJournalTableAndAForeignTable_FailsClosed()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await SchemaMigrationTestSupport.CreateForeignTableAsync(connectionString);

            using var host = new SchemaMigrationTestHost(connectionString);
            await host.CreateEmptyJournalAsync(TestContext.Current.CancellationToken);
            var snapshotBefore =
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString);

            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                host.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            Assert.Equal(SchemaMigrationErrorCodes.LegacySchemaUnrecognized, exception.ErrorCode);
            Assert.Contains(
                SchemaMigrationTestSupport.ForeignTableName,
                exception.Message,
                StringComparison.Ordinal);
            Assert.Empty(await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));
            Assert.Equal(
                snapshotBefore,
                await SchemaMigrationTestSupport.ReadSchemaSnapshotAsync(connectionString));
        }
        finally
        {
            await PostgresSchemaTestHelper.RebuildSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task GetStatus_ReportsPendingVersionsBeforeMigratingAndUpToDateAfter()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
            await PostgresSchemaTestHelper.EnableVectorExtensionAsync(connectionString);

            using var host = new SchemaMigrationTestHost(connectionString);
            var before = await host.Migrator.GetStatusAsync(TestContext.Current.CancellationToken);

            Assert.False(before.JournalPresent);
            Assert.Null(before.JournalHead);
            Assert.False(before.IsUpToDate);
            Assert.Equal(
                host.Catalog.Scripts.Select(static script => script.Version),
                before.PendingVersions);
            Assert.Empty(before.Mismatches);

            await host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);
            var after = await host.Migrator.GetStatusAsync(TestContext.Current.CancellationToken);

            Assert.True(after.JournalPresent);
            Assert.True(after.IsUpToDate);
            Assert.Equal(host.Catalog.Head, after.JournalHead);
            Assert.Equal(host.Catalog.Head, after.PackagedHead);
            Assert.Empty(after.PendingVersions);
            Assert.Empty(after.Mismatches);
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        }
    }
}
