using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Infrastructure.Documents.Postgres.Shared;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Documents.Postgres.IndexingJobs;

internal static class PostgresIndexingJobMapper
{
    public static IndexingJob Map(NpgsqlDataReader reader)
    {
        return new IndexingJob(
            reader.GetGuid(0),
            reader.GetGuid(1),
            Enum.Parse<IndexingJobStatus>(reader.GetString(2)),
            reader.GetInt32(3),
            reader.GetInt32(4),
            PostgresTimestampReader.GetDateTimeOffset(reader, 5),
            PostgresTimestampReader.GetDateTimeOffset(reader, 6),
            PostgresTimestampReader.GetDateTimeOffset(reader, 7),
            reader.IsDBNull(8) ? null : PostgresTimestampReader.GetDateTimeOffset(reader, 8),
            reader.IsDBNull(9) ? null : PostgresTimestampReader.GetDateTimeOffset(reader, 9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }
}
