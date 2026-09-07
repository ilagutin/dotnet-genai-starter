using GenAIPlatform.Infrastructure.Postgres;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;

internal sealed class PostgresDocumentIngestionConnectionFactory(PostgresDataSourceProvider dataSourceProvider)
{
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        return await dataSourceProvider.OpenConnectionAsync(cancellationToken);
    }
}
