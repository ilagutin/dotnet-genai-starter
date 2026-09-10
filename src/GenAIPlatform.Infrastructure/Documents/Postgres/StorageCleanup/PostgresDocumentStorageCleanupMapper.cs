using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.StorageCleanup;

internal static class PostgresDocumentStorageCleanupMapper
{
    public static DocumentStorageCleanupRequest Map(NpgsqlDataReader reader)
    {
        return new DocumentStorageCleanupRequest(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            PostgresTimestampReader.GetDateTimeOffset(reader, 6),
            reader.GetString(7),
            Enum.Parse<DocumentStorageCleanupStatus>(reader.GetString(8)),
            reader.GetInt32(9),
            PostgresTimestampReader.GetDateTimeOffset(reader, 10),
            PostgresTimestampReader.GetDateTimeOffset(reader, 11),
            PostgresTimestampReader.GetDateTimeOffset(reader, 12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
    }
}
