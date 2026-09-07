using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.Metadata;

internal static class PostgresDocumentMapper
{
    public static Document Map(NpgsqlDataReader reader)
    {
        return new Document(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.GetString(9),
            reader.GetInt32(10),
            Enum.Parse<DocumentAccessLevel>(reader.GetString(11)),
            Enum.Parse<DocumentIndexingStatus>(reader.GetString(12)),
            PostgresTimestampReader.GetDateTimeOffset(reader, 13),
            PostgresTimestampReader.GetDateTimeOffset(reader, 14),
            reader.IsDBNull(15) ? null : reader.GetString(15));
    }
}
