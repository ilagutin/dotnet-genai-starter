using GenAIPlatform.Infrastructure.Migrations;
using Npgsql;

namespace GenAIPlatform.UnitTests;

public sealed class SchemaMigrationErrorMapperTests
{
    private const string Sentinel = "Key (document_id)=(zzsentinelzz)";

    [Fact]
    public void DescribeNonPreconditionPostgresExceptionOmitsMessageText()
    {
        var exception = new PostgresException(
            $"duplicate key value violates unique constraint \"documents_pkey\" {Sentinel}",
            "ERROR",
            "ERROR",
            "23505");

        var described = SchemaMigrationErrorMapper.Describe(exception);

        Assert.Equal("PostgreSQL error 23505", described);
        Assert.DoesNotContain(Sentinel, described, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribePreconditionPostgresExceptionJoinsMessageTextOntoOneLine()
    {
        var exception = new PostgresException(
            "12 chunks across 3 documents have no embedding_vector.\r\nRe-index those documents or delete those chunks, then run the migration again.",
            "ERROR",
            "ERROR",
            MigrationNames.PreconditionSqlState);

        var described = SchemaMigrationErrorMapper.Describe(exception);

        Assert.Equal(
            "PostgreSQL error GN001: 12 chunks across 3 documents have no embedding_vector. " +
            "Re-index those documents or delete those chunks, then run the migration again.",
            described);
        Assert.DoesNotContain('\n', described);
        Assert.DoesNotContain('\r', described);
    }

    [Fact]
    public void DescribeNonPostgresNpgsqlExceptionCarriesNoMessageText()
    {
        const string secret = "PRIVATE_CONNECTION_DETAIL";
        var exception = new NpgsqlException(secret);

        var described = SchemaMigrationErrorMapper.Describe(exception);

        Assert.Equal("PostgreSQL connection error", described);
        Assert.DoesNotContain(secret, described, StringComparison.Ordinal);
    }
}
