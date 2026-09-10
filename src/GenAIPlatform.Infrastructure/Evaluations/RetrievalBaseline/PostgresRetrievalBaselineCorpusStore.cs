using GenAIPlatform.Application.Evaluations.RetrievalBaseline;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;
using GenAIPlatform.Domain.Evaluations.Retrieval;
using GenAIPlatform.Infrastructure.Postgres;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Evaluations.RetrievalBaseline;

/// <summary>
/// PostgreSQL adapter for the retrieval baseline corpus. Npgsql failures are normalized
/// into <see cref="RetrievalBaselineStoreException" /> at this port boundary, and neither
/// the exception nor the log record carries corpus text, vectors or connection strings.
/// </summary>
internal sealed partial class PostgresRetrievalBaselineCorpusStore(
    PostgresDataSourceProvider dataSourceProvider,
    ILogger<PostgresRetrievalBaselineCorpusStore> logger)
    : IRetrievalBaselineCorpusStore
{
    private const string ProviderName = "postgres";
    private const string UnavailableErrorCode = "retrieval_baseline_store_unavailable";
    private const string WriteFailedErrorCode = "retrieval_baseline_store_write_failed";
    private const string ReadFailedErrorCode = "retrieval_baseline_store_read_failed";

    public async Task ReplaceCorpusAsync(
        RetrievalBaselineCorpus corpus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        if (corpus.TenantIds.Count == 0)
        {
            throw new RetrievalBaselineStoreException(
                ProviderName,
                "Retrieval baseline corpus must name at least one benchmark tenant.",
                WriteFailedErrorCode);
        }

        EnsureBenchmarkTenants(corpus);

        await ExecuteAsync(
            connection => PostgresRetrievalBaselineCorpusWriter.ReplaceAsync(
                connection,
                corpus,
                cancellationToken),
            WriteFailedErrorCode,
            cancellationToken);
    }

    public async Task<RetrievalBaselineEnvironment> GetEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteAsync(
            connection => PostgresRetrievalBaselineEnvironmentReader.ReadAsync(connection, cancellationToken),
            ReadFailedErrorCode,
            cancellationToken);
    }

    /// <summary>
    /// Fails closed before the first statement runs when any tenant the corpus names is
    /// outside the fixed benchmark namespace. The dataset validator already enforces this,
    /// but the delete this store issues is the destructive step, so it does not rely on a
    /// caller having validated its input.
    /// </summary>
    private static void EnsureBenchmarkTenants(RetrievalBaselineCorpus corpus)
    {
        var tenantIds = corpus.TenantIds
            .Concat(corpus.Documents.Select(static document => document.TenantId));

        foreach (var tenantId in tenantIds)
        {
            if (!RetrievalBaselineTenants.IsBenchmarkTenant(tenantId))
            {
                throw new RetrievalBaselineStoreException(
                    ProviderName,
                    $"Retrieval baseline corpus names tenant '{tenantId}', which is outside the fixed benchmark tenant namespace '{RetrievalBaselineTenants.RequiredPrefix}'.",
                    WriteFailedErrorCode);
            }
        }
    }

    private async Task ExecuteAsync(
        Func<NpgsqlConnection, Task> operation,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync<object?>(
            async connection =>
            {
                await operation(connection);
                return null;
            },
            errorCode,
            cancellationToken);
    }

    private async Task<TResult> ExecuteAsync<TResult>(
        Func<NpgsqlConnection, Task<TResult>> operation,
        string errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            return await operation(connection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RetrievalBaselineStoreException)
        {
            throw;
        }
        catch (PostgresException exception)
        {
            throw Fail(
                "Retrieval baseline store operation failed.",
                errorCode,
                exception);
        }
        catch (NpgsqlException exception)
        {
            throw Fail("Retrieval baseline store is unavailable.", UnavailableErrorCode, exception);
        }
        catch (TimeoutException exception)
        {
            throw Fail("Retrieval baseline store is unavailable.", UnavailableErrorCode, exception);
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        }
        catch (PostgresConnectionConfigurationException exception) when (exception.IsMissing)
        {
            throw Fail(
                "Retrieval baseline store is not configured.",
                UnavailableErrorCode,
                innerException: null);
        }
        catch (PostgresConnectionConfigurationException exception)
        {
            throw Fail("Retrieval baseline store is unavailable.", UnavailableErrorCode, exception);
        }
    }

    private RetrievalBaselineStoreException Fail(
        string message,
        string errorCode,
        Exception? innerException)
    {
        LogStoreFailure(logger, errorCode, innerException?.GetType().Name ?? "none");
        return new RetrievalBaselineStoreException(ProviderName, message, errorCode, innerException);
    }

    [LoggerMessage(
        EventId = 5101,
        EventName = "RetrievalBaselineStoreFailure",
        Level = LogLevel.Warning,
        Message = "Retrieval baseline store failure: {ErrorCode} ({ExceptionType})")]
    private static partial void LogStoreFailure(ILogger logger, string errorCode, string exceptionType);
}
