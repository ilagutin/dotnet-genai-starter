using GenAIPlatform.Infrastructure.Migrations;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresSchemaMigrationFaultTests(PostgresRepositoryFixture postgres)
{
    /// <summary>
    /// One past the packaged head, so the probe extends the real catalog instead of shadowing a
    /// released migration. Bump it when a migration is added.
    /// </summary>
    private const string ProbeVersion = "0008";
    private const string ProbeTableName = "migration_probe";

    /// <summary>
    /// A syntactically valid connection to a port nothing listens on, so opening it fails the way
    /// a lost database does rather than as a configuration error.
    /// </summary>
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=genai_platform;Username=genai;Password=genai;Timeout=2";

    [DockerAvailableFact]
    public async Task Migrate_WhenAScriptFails_RollsBackAndRecordsAFailedAttempt()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await PrepareEmptyDatabaseAsync(connectionString);

            using var host = new SchemaMigrationTestHost(
                connectionString,
                CreateExtendedCatalog(
                    $"CREATE TABLE genai.{ProbeTableName} (id uuid PRIMARY KEY);\nSELECT 1 / 0;"));
            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                host.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            Assert.Equal(SchemaMigrationErrorCodes.ApplyFailed, exception.ErrorCode);
            Assert.Contains($"'{ProbeVersion}'", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(ProbeTableName, exception.Message, StringComparison.Ordinal);
            Assert.Contains("rolled back", exception.Message, StringComparison.Ordinal);

            Assert.False(await SchemaMigrationTestSupport.TableExistsAsync(
                connectionString,
                ProbeTableName));
            var journal = await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);
            Assert.DoesNotContain(ProbeVersion, journal.Select(static entry => entry.Version));

            var attempt = Assert.Single(
                await SchemaMigrationTestSupport.ReadAttemptsAsync(connectionString));
            Assert.Equal(ProbeVersion, attempt.Version);
            Assert.Equal("rolled-back", attempt.Outcome);
            Assert.Contains("22012", attempt.ErrorSummary, StringComparison.Ordinal);
        }
        finally
        {
            await RestoreSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WhenTheCommitAcknowledgementIsLost_ReportsUncertaintyAndRestartReconciles()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await PrepareEmptyDatabaseAsync(connectionString);
            var catalog = CreateExtendedCatalog(
                $"CREATE TABLE IF NOT EXISTS genai.{ProbeTableName} (id uuid PRIMARY KEY);");

            using (var failingHost = new SchemaMigrationTestHost(
                connectionString,
                catalog,
                new ThrowingMigrationCommitObserver(ProbeVersion)))
            {
                var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                    failingHost.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

                Assert.Equal(SchemaMigrationErrorCodes.ApplyFailed, exception.ErrorCode);
                Assert.Contains(
                    "may or may not have been applied",
                    exception.Message,
                    StringComparison.Ordinal);
            }

            var journalAfterFailure =
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);
            Assert.Contains(ProbeVersion, journalAfterFailure.Select(static entry => entry.Version));
            var attempt = Assert.Single(
                await SchemaMigrationTestSupport.ReadAttemptsAsync(connectionString));
            Assert.Equal(ProbeVersion, attempt.Version);
            Assert.Equal("unknown", attempt.Outcome);

            using var restartedHost = new SchemaMigrationTestHost(connectionString, catalog);
            var result = await restartedHost.Migrator.MigrateAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(result.AppliedVersions);
            Assert.Equal(ProbeVersion, result.JournalHead);
            Assert.Equal(
                journalAfterFailure,
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));
        }
        finally
        {
            await RestoreSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WhenAnAppliedScriptChanged_FailsWithAChecksumMismatch()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await PrepareEmptyDatabaseAsync(connectionString);
            using (var host = new SchemaMigrationTestHost(connectionString))
            {
                await host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);
            }

            var journalBefore = await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);
            var definitions = SchemaMigrationTestSupport.PackagedDefinitions().ToArray();
            var editedVersion = definitions[^1].Version;
            definitions[^1] = definitions[^1] with
            {
                Sql = definitions[^1].Sql + "\n-- edited after release\n"
            };

            using var editedHost = new SchemaMigrationTestHost(
                connectionString,
                MigrationCatalog.Create(definitions));
            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                editedHost.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            Assert.Equal(SchemaMigrationErrorCodes.ChecksumMismatch, exception.ErrorCode);
            Assert.Contains($"'{editedVersion}'", exception.Message, StringComparison.Ordinal);
            Assert.Equal(
                journalBefore,
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));
        }
        finally
        {
            await RestoreSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WhenTheJournalHasAnUnknownVersion_FailsWithoutTouchingTheJournal()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await PrepareEmptyDatabaseAsync(connectionString);
            using (var newerHost = new SchemaMigrationTestHost(
                connectionString,
                CreateExtendedCatalog(
                    $"CREATE TABLE IF NOT EXISTS genai.{ProbeTableName} (id uuid PRIMARY KEY);")))
            {
                await newerHost.Migrator.MigrateAsync(TestContext.Current.CancellationToken);
            }

            var journalBefore = await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);

            using var packagedHost = new SchemaMigrationTestHost(connectionString);
            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                packagedHost.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            Assert.Equal(SchemaMigrationErrorCodes.UnknownVersion, exception.ErrorCode);
            Assert.Contains($"'{ProbeVersion}'", exception.Message, StringComparison.Ordinal);
            Assert.Equal(
                journalBefore,
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));
        }
        finally
        {
            await RestoreSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WithTwoConcurrentRunners_AppliesEveryVersionExactlyOnce()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await PrepareEmptyDatabaseAsync(connectionString);

            using var firstHost = new SchemaMigrationTestHost(connectionString);
            using var secondHost = new SchemaMigrationTestHost(connectionString);
            var results = await Task.WhenAll(
                firstHost.Migrator.MigrateAsync(TestContext.Current.CancellationToken),
                secondHost.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            var appliedVersions = results
                .SelectMany(static result => result.AppliedVersions)
                .ToArray();

            Assert.Equal(firstHost.Catalog.Scripts.Count, appliedVersions.Length);
            Assert.Equal(appliedVersions.Length, appliedVersions.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(results, static result => result.AppliedVersions.Count == 0);
            Assert.All(results, result => Assert.Equal(firstHost.Catalog.Head, result.JournalHead));
            Assert.Equal(0L, await SchemaMigrationTestSupport.CountMigrationAdvisoryLocksAsync(
                connectionString));
        }
        finally
        {
            await RestoreSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WhileTheLockIsHeld_WaitsAndThenMigratesAfterItIsReleased()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await PrepareEmptyDatabaseAsync(connectionString);
            await using var heldLock = await HeldMigrationAdvisoryLock.AcquireAsync(connectionString);

            using var host = new SchemaMigrationTestHost(
                connectionString,
                lockTimeout: TimeSpan.FromSeconds(30));
            var migrateTask = host.Migrator.MigrateAsync(TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken);

            Assert.False(migrateTask.IsCompleted);
            Assert.False(await SchemaMigrationTestSupport.TableExistsAsync(
                connectionString,
                "schema_migrations"));

            await heldLock.ReleaseAsync();
            var result = await migrateTask;

            Assert.Equal(host.Catalog.Scripts.Count, result.AppliedVersions.Count);
            Assert.Equal(0L, await SchemaMigrationTestSupport.CountMigrationAdvisoryLocksAsync(
                connectionString));
        }
        finally
        {
            await RestoreSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WhenTheLockTimeoutElapses_LeavesTheJournalAndSessionLocksAlone()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await PrepareEmptyDatabaseAsync(connectionString);
            await using var heldLock = await HeldMigrationAdvisoryLock.AcquireAsync(connectionString);

            using var host = new SchemaMigrationTestHost(
                connectionString,
                lockTimeout: TimeSpan.FromSeconds(1));
            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                host.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            Assert.Equal(SchemaMigrationErrorCodes.LockTimeout, exception.ErrorCode);
            Assert.False(await SchemaMigrationTestSupport.TableExistsAsync(
                connectionString,
                "schema_migrations"));
            Assert.Equal(1L, await SchemaMigrationTestSupport.CountMigrationAdvisoryLocksAsync(
                connectionString));

            await heldLock.ReleaseAsync();

            Assert.Equal(0L, await SchemaMigrationTestSupport.CountMigrationAdvisoryLocksAsync(
                connectionString));
        }
        finally
        {
            await RestoreSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WhenCanceledWhileWaiting_LeavesTheJournalAndSessionLocksAlone()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await PrepareEmptyDatabaseAsync(connectionString);
            await using var heldLock = await HeldMigrationAdvisoryLock.AcquireAsync(connectionString);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            using var host = new SchemaMigrationTestHost(
                connectionString,
                lockTimeout: TimeSpan.FromSeconds(30));
            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                host.Migrator.MigrateAsync(cancellation.Token));

            Assert.Equal(SchemaMigrationErrorCodes.Canceled, exception.ErrorCode);
            Assert.False(await SchemaMigrationTestSupport.TableExistsAsync(
                connectionString,
                "schema_migrations"));
            Assert.Equal(1L, await SchemaMigrationTestSupport.CountMigrationAdvisoryLocksAsync(
                connectionString));

            await heldLock.ReleaseAsync();

            Assert.Equal(0L, await SchemaMigrationTestSupport.CountMigrationAdvisoryLocksAsync(
                connectionString));
        }
        finally
        {
            await RestoreSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WhenTheAttemptRecordCannotBeWritten_ReportsBothFailures()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await PrepareEmptyDatabaseAsync(connectionString);

            using var host = new SchemaMigrationTestHost(
                connectionString,
                CreateExtendedCatalog(
                    $"CREATE TABLE genai.{ProbeTableName} (id uuid PRIMARY KEY);\nSELECT 1 / 0;"),
                attemptRecordConnectionString: UnreachableConnectionString);
            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                host.Migrator.MigrateAsync(TestContext.Current.CancellationToken));

            Assert.Equal(SchemaMigrationErrorCodes.ApplyFailed, exception.ErrorCode);
            Assert.Contains("PostgreSQL error 22012", exception.Message, StringComparison.Ordinal);
            Assert.Contains(
                "The failure record could not be written either (PostgreSQL connection error)",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains(
                "does not describe this failure",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Empty(await SchemaMigrationTestSupport.ReadAttemptsAsync(connectionString));
            var journal = await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);
            Assert.DoesNotContain(ProbeVersion, journal.Select(static entry => entry.Version));
        }
        finally
        {
            await RestoreSchemaAsync(connectionString);
        }
    }

    [DockerAvailableFact]
    public async Task Migrate_WhenCanceledWhileApplying_ReportsCancellationAndLeavesTheJournalAlone()
    {
        var connectionString = await postgres.GetConnectionStringAsync();

        try
        {
            await PrepareEmptyDatabaseAsync(connectionString);
            using (var currentHost = new SchemaMigrationTestHost(connectionString))
            {
                await currentHost.Migrator.MigrateAsync(TestContext.Current.CancellationToken);
            }

            var journalBefore = await SchemaMigrationTestSupport.ReadJournalAsync(connectionString);

            using var slowHost = new SchemaMigrationTestHost(
                connectionString,
                CreateExtendedCatalog("SELECT pg_sleep(5);"));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var exception = await Assert.ThrowsAsync<SchemaMigrationException>(() =>
                slowHost.Migrator.MigrateAsync(cancellation.Token));

            Assert.Equal(SchemaMigrationErrorCodes.Canceled, exception.ErrorCode);
            Assert.Contains($"'{ProbeVersion}'", exception.Message, StringComparison.Ordinal);
            Assert.Equal(
                journalBefore,
                await SchemaMigrationTestSupport.ReadJournalAsync(connectionString));
            Assert.Equal(0L, await SchemaMigrationTestSupport.CountMigrationAdvisoryLocksAsync(
                connectionString));
        }
        finally
        {
            await RestoreSchemaAsync(connectionString);
        }
    }

    private static MigrationCatalog CreateExtendedCatalog(string probeSql)
    {
        return MigrationCatalog.Create(
        [
            .. SchemaMigrationTestSupport.PackagedDefinitions(),
            new MigrationScriptDefinition(ProbeVersion, "test-only-probe", probeSql)
        ]);
    }

    private static async Task PrepareEmptyDatabaseAsync(string connectionString)
    {
        await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
        await PostgresSchemaTestHelper.EnableVectorExtensionAsync(connectionString);
    }

    private static async Task RestoreSchemaAsync(string connectionString)
    {
        await SchemaMigrationTestSupport.ResetSchemaAsync(connectionString);
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
    }
}
