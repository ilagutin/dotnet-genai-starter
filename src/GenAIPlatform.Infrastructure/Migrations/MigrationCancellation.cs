using Npgsql;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Recognizes a canceled migration. Npgsql usually surfaces a canceled command as an
/// <see cref="OperationCanceledException"/>, but a cancel request that the backend acknowledges
/// first arrives as SQLSTATE 57014; both mean the operator stopped the run, not that the
/// migration failed.
/// </summary>
internal static class MigrationCancellation
{
    private const string QueryCanceledSqlState = "57014";

    public static bool IsCancellation(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return true;
        }

        return cancellationToken.IsCancellationRequested &&
               exception is PostgresException postgres &&
               string.Equals(postgres.SqlState, QueryCanceledSqlState, StringComparison.Ordinal);
    }
}
