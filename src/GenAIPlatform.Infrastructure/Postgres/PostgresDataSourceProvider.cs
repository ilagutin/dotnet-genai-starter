using GenAIPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Postgres;

internal sealed class PostgresDataSourceProvider : IDisposable
{
    private readonly IConfiguration configuration;
    private readonly IOptions<PostgresOptions> options;
    private readonly Lazy<NpgsqlDataSource> dataSource;

    public PostgresDataSourceProvider(
        IConfiguration configuration,
        IOptions<PostgresOptions> options)
    {
        this.configuration = configuration;
        this.options = options;
        dataSource = new Lazy<NpgsqlDataSource>(
            CreateDataSource,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        return await dataSource.Value.OpenConnectionAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (dataSource.IsValueCreated)
        {
            dataSource.Value.Dispose();
        }
    }

    private NpgsqlDataSource CreateDataSource()
    {
        var connectionStringName = options.Value.ConnectionStringName;
        var connectionString = configuration.GetConnectionString(connectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw PostgresConnectionConfigurationException.Missing(connectionStringName);
        }

        try
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseVector();
            return builder.Build();
        }
        catch (Exception exception) when (IsConnectionConfigurationException(exception))
        {
            throw PostgresConnectionConfigurationException.Invalid(connectionStringName);
        }
    }

    private static bool IsConnectionConfigurationException(Exception exception)
    {
        return exception is ArgumentException or InvalidOperationException or NotSupportedException;
    }
}
