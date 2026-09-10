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
            Assert.Equal(VersionsAfterTheFrozenBaseline(host), result.AppliedVersions);
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

            // 0007 converged both databases on one embedding representation, and the seeded
            // legacy chunk kept its embedding as a vector rather than being dropped.
            Assert.False(await SchemaMigrationTestSupport.ColumnExistsAsync(
                connectionString,
                "document_chunks",
                "embedding_values"));
            Assert.Equal(0L, await SchemaMigrationTestSupport.CountUnvectorizedChunksAsync(
                connectionString));

            AssertJournalMatchesTheAdoptedBaseline(
                host,
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));
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
            Assert.Equal(VersionsAfterTheFrozenBaseline(host), result.AppliedVersions);

            AssertJournalMatchesTheAdoptedBaseline(
                host,
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));
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

            using var host = new SchemaMigrationTestHost(connectionString);
            await host.CreateEmptyJournalAsync(TestContext.Current.CancellationToken);

            var result = await host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

            // Adoption still journals the frozen baseline instead of replaying it; only the
            // versions released after v0.3.1 are applied, and they leave the rows alone.
            Assert.True(result.AdoptedLegacySchema);
            Assert.Equal(VersionsAfterTheFrozenBaseline(host), result.AppliedVersions);
            Assert.Equal(host.Catalog.Head, result.JournalHead);
            Assert.Equal(
                seededRowCounts,
                await LegacyV031SeedData.ReadRowCountsAsync(connectionString));
            AssertJournalMatchesTheAdoptedBaseline(
                host,
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));
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

    /// <summary>
    /// The versions an adopted v0.3.1 database still has to apply, derived from the frozen
    /// baseline instead of hard-coded, so a later release extends the expectation on its own.
    /// </summary>
    private static IReadOnlyList<string> VersionsAfterTheFrozenBaseline(SchemaMigrationTestHost host)
    {
        return
        [
            .. host.Catalog.Scripts
                .Where(static script => !LegacyV031Baseline.Checksums.ContainsKey(script.Version))
                .Select(static script => script.Version)
        ];
    }

    /// <summary>
    /// An adopted journal carries the frozen checksums for the baseline versions and the packaged
    /// checksum for everything applied after it, and only the baseline rows are marked adopted.
    /// </summary>
    private static void AssertJournalMatchesTheAdoptedBaseline(
        SchemaMigrationTestHost host,
        IReadOnlyList<MigrationJournalEntry> journal)
    {
        Assert.Equal(
            host.Catalog.Scripts.Select(static script =>
                LegacyV031Baseline.Checksums.GetValueOrDefault(script.Version, script.Checksum)),
            journal.Select(static entry => entry.Checksum));
        Assert.Equal(
            host.Catalog.Scripts.Select(static script =>
                LegacyV031Baseline.Checksums.ContainsKey(script.Version)),
            journal.Select(static entry => entry.Adopted));
    }
}
