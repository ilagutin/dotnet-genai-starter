using System.Diagnostics;
using GenAIPlatform.Infrastructure.Configuration;
using GenAIPlatform.Infrastructure.Postgres;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Applies packaged migrations in order under a bounded advisory lock, recording each applied
/// version and its checksum in the durable journal inside the same transaction as the schema
/// change. Recovery invariant: the journal is read before anything is applied, so a run that
/// lost its commit acknowledgement replays only versions the journal does not contain, and every
/// packaged migration is written to be idempotent so that replay is safe.
/// </summary>
internal sealed class PostgresSchemaMigrator(
    PostgresDataSourceProvider dataSourceProvider,
    IMigrationCatalog catalog,
    MigrationJournalStore journalStore,
    PostgresAdvisoryLock advisoryLock,
    LegacySchemaAdopter legacySchemaAdopter,
    SchemaMigrationErrorMapper errorMapper,
    MigrationCommitObserver commitObserver,
    IOptions<MigrationOptions> options,
    TimeProvider timeProvider) : ISchemaMigrator
{
    public async Task<SchemaMigrationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await MigrateCoreAsync(cancellationToken);
        }
        catch (PostgresConnectionConfigurationException exception)
        {
            throw errorMapper.NotConfigured(exception);
        }
        catch (Exception exception)
            when (MigrationCancellation.IsCancellation(exception, cancellationToken))
        {
            throw errorMapper.Canceled(exception);
        }
        catch (PostgresException exception)
        {
            throw errorMapper.StoreFailed(exception);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            throw errorMapper.Unavailable(exception);
        }
    }

    public async Task<SchemaMigrationStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetStatusCoreAsync(cancellationToken);
        }
        catch (PostgresConnectionConfigurationException exception)
        {
            throw errorMapper.NotConfigured(exception);
        }
        catch (Exception exception)
            when (MigrationCancellation.IsCancellation(exception, cancellationToken))
        {
            throw errorMapper.Canceled(exception);
        }
        catch (PostgresException exception)
        {
            throw errorMapper.StoreFailed(exception);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            throw errorMapper.Unavailable(exception);
        }
    }

    private async Task<SchemaMigrationResult> MigrateCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await advisoryLock.AcquireAsync(connection, options.Value.LockTimeout, cancellationToken);
        try
        {
            var adopted = await PrepareJournalAsync(connection, cancellationToken);
            var entries = await journalStore.ReadAsync(connection, cancellationToken);
            MigrationJournalValidator.EnsureConsistent(catalog, entries);

            var appliedVersions = new List<string>();
            foreach (var version in MigrationJournalValidator.FindPendingVersions(catalog, entries))
            {
                await ApplyAsync(connection, catalog.Find(version)!, cancellationToken);
                appliedVersions.Add(version);
            }

            var head = await journalStore.ReadHeadAsync(connection, cancellationToken);

            return new SchemaMigrationResult(appliedVersions, adopted, head);
        }
        finally
        {
            await advisoryLock.ReleaseAsync(connection);
        }
    }

    private async Task<SchemaMigrationStatus> GetStatusCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        if (!await journalStore.JournalExistsAsync(connection, cancellationToken))
        {
            return new SchemaMigrationStatus(
                JournalPresent: false,
                JournalHead: null,
                catalog.Head,
                [.. catalog.Scripts.Select(static script => script.Version)],
                []);
        }

        var entries = await journalStore.ReadAsync(connection, cancellationToken);

        return new SchemaMigrationStatus(
            JournalPresent: true,
            entries.Count == 0 ? null : entries[^1].Version,
            catalog.Head,
            MigrationJournalValidator.FindPendingVersions(catalog, entries),
            [
                .. MigrationJournalValidator
                    .FindMismatches(catalog, entries)
                    .Select(static mismatch => mismatch.Description)
            ]);
    }

    private async Task<bool> PrepareJournalAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        // An empty journal decides nothing, whether or not the table exists: an interrupted first
        // run can leave the table behind with no rows. Only a journal that already names a
        // migration proves this database was migrated by this runner.
        if (await journalStore.HasEntriesAsync(connection, cancellationToken))
        {
            await journalStore.EnsureJournalAsync(connection, cancellationToken);
            return false;
        }

        // The fingerprint describes only migration-owned tables, so it classifies the existing
        // installation by what the released scripts left behind and ignores a journal table this
        // run or an interrupted predecessor created.
        var decision = await legacySchemaAdopter.DecideAsync(connection, cancellationToken);
        if (decision == LegacySchemaDecision.Fresh)
        {
            await journalStore.EnsureJournalAsync(connection, cancellationToken);
            return false;
        }

        await journalStore.CreateJournalWithAdoptedRowsAsync(
            connection,
            BuildAdoptedJournalRows(),
            timeProvider.GetUtcNow(),
            cancellationToken);

        return true;
    }

    /// <summary>
    /// Adoption records the frozen v0.3.1 checksums, not the packaged ones. If a released
    /// migration script is ever edited, an adopted database reports a checksum mismatch instead
    /// of silently claiming a schema it does not have.
    /// </summary>
    private List<MigrationScript> BuildAdoptedJournalRows()
    {
        var rows = new List<MigrationScript>();
        foreach (var script in catalog.Scripts)
        {
            if (string.CompareOrdinal(script.Version, LegacyV031Baseline.AdoptedThroughVersion) > 0)
            {
                break;
            }

            if (!LegacyV031Baseline.Checksums.TryGetValue(script.Version, out var frozenChecksum))
            {
                throw MigrationCatalog.Invalid(
                    $"The frozen {MigrationNames.LegacySourceVersion} baseline has no checksum for " +
                    $"migration '{script.Version}'.");
            }

            rows.Add(script with { Checksum = frozenChecksum });
        }

        return rows;
    }

    private async Task ApplyAsync(
        NpgsqlConnection connection,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = timeProvider.GetUtcNow();
        var startedTimestamp = Stopwatch.GetTimestamp();
        var commitSent = false;
        try
        {
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await using (var command = new NpgsqlCommand(script.Sql, connection, transaction))
                {
                    command.CommandTimeout = options.Value.CommandTimeoutSeconds;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await journalStore.InsertAppliedAsync(
                    connection,
                    transaction,
                    script,
                    timeProvider.GetUtcNow(),
                    (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
                    adopted: false,
                    cancellationToken);

                commitSent = true;
                await transaction.CommitAsync(cancellationToken);
            }

            await commitObserver.AfterCommitAsync(script.Version, cancellationToken);
        }
        catch (Exception exception) when (exception is not SchemaMigrationException)
        {
            var outcome = commitSent
                ? MigrationAttemptOutcome.Unknown
                : MigrationAttemptOutcome.RolledBack;
            var canceled = MigrationCancellation.IsCancellation(exception, cancellationToken);
            var description = SchemaMigrationErrorMapper.Describe(exception);
            var errorSummary = canceled
                ? $"Applying migration '{script.Version}' was canceled: {description}."
                : $"Applying migration '{script.Version}' failed: {description}.";
            // The attempt record is written without the caller's token: a canceled run must still
            // leave the audit row that explains why this version is missing from the journal.
            var attemptRecordFailure = await journalStore.TryRecordAttemptAsync(
                new MigrationAttemptRecord(
                    script.Version,
                    startedAtUtc,
                    timeProvider.GetUtcNow(),
                    outcome,
                    exception.GetType().Name,
                    errorSummary),
                CancellationToken.None);

            throw canceled
                ? errorMapper.ApplyCanceled(script.Version, outcome, exception, attemptRecordFailure)
                : errorMapper.ApplyFailed(script.Version, outcome, exception, attemptRecordFailure);
        }
    }
}
