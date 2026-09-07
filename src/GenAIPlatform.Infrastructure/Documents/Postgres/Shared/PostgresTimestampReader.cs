using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.Shared;

internal static class PostgresTimestampReader
{
    public static DateTimeOffset GetDateTimeOffset(
        NpgsqlDataReader reader,
        int ordinal)
    {
        var value = reader.GetDateTime(ordinal);
        return value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value)
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
