using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Infrastructure.Migrations;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Retrieval;

internal sealed class RagVectorSearchErrorMapper
{
    public RagVectorSearchException MapPostgres(PostgresException exception)
    {
        var errorCode = IsSchemaError(exception.SqlState)
            ? "retrieval_schema_error"
            : "retrieval_query_failed";
        var message = errorCode == "retrieval_schema_error"
            ? "RAG retrieval schema is not ready."
            : "RAG retrieval query failed.";

        return new RagVectorSearchException(
            PostgresRagConnectionFactory.ProviderName,
            message,
            errorCode,
            exception);
    }

    public RagVectorSearchException Unavailable(Exception exception)
    {
        return new RagVectorSearchException(
            PostgresRagConnectionFactory.ProviderName,
            "RAG retrieval store is unavailable.",
            errorCode: "retrieval_unavailable",
            exception);
    }

    public RagVectorSearchException QueryFailed(
        string message,
        Exception exception)
    {
        return new RagVectorSearchException(
            PostgresRagConnectionFactory.ProviderName,
            message,
            errorCode: "retrieval_query_failed",
            exception);
    }

    public static RagVectorSearchException InvalidQuery(string message)
    {
        return new RagVectorSearchException(
            PostgresRagConnectionFactory.ProviderName,
            message,
            errorCode: "retrieval_query_failed");
    }

    public static RagVectorSearchException SchemaNotReady()
    {
        return new RagVectorSearchException(
            PostgresRagConnectionFactory.ProviderName,
            $"RAG retrieval schema is not ready. Run `{MigrationNames.MigrateCommand}`.",
            errorCode: "retrieval_schema_error");
    }

    private static bool IsSchemaError(string? sqlState)
    {
        return sqlState is
            "42P01" or
            "42703" or
            "42883" or
            "42704";
    }
}
