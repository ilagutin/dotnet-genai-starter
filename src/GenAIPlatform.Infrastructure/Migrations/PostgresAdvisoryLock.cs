using Npgsql;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Bounded session-level advisory lock around the migration journal. Two runners serialize:
/// the second waits, then sees the first runner's committed journal and applies nothing.
/// The lock is always released on the same connection that took it.
/// </summary>
internal sealed class PostgresAdvisoryLock
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public async Task AcquireAsync(
        NpgsqlConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryAcquireAsync(connection, cancellationToken))
                {
                    return;
                }

                if (DateTimeOffset.UtcNow + PollInterval > deadline)
                {
                    throw new SchemaMigrationException(
                        $"Another schema migration run holds the {MigrationNames.AdvisoryLockKey} advisory " +
                        $"lock. Waited {timeout.TotalSeconds:0.###} seconds; the migration journal was not changed.",
                        SchemaMigrationErrorCodes.LockTimeout);
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException exception)
        {
            throw new SchemaMigrationException(
                "Schema migration was canceled while waiting for the " +
                $"{MigrationNames.AdvisoryLockKey} advisory lock; the migration journal was not changed.",
                SchemaMigrationErrorCodes.Canceled,
                exception);
        }
    }

    /// <summary>
    /// Releases the lock without a caller token: a canceled or failed run must still hand the
    /// lock back on this session instead of leaving it for the connection teardown. A release
    /// that cannot run because the session itself is broken is ignored rather than allowed to
    /// mask the failure that is already propagating; PostgreSQL drops session locks with the
    /// session.
    /// </summary>
    public async Task ReleaseAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext(@lock_key)::bigint);",
                connection);
            command.Parameters.AddWithValue("lock_key", MigrationNames.AdvisoryLockKey);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            // The session cannot answer any more; its advisory locks are gone with it.
        }
    }

    private static async Task<bool> TryAcquireAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(hashtext(@lock_key)::bigint);",
            connection);
        command.Parameters.AddWithValue("lock_key", MigrationNames.AdvisoryLockKey);
        var acquired = await command.ExecuteScalarAsync(cancellationToken);

        return acquired is true;
    }
}
