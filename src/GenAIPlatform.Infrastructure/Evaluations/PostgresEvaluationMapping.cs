using GenAIPlatform.Application.Evaluations.StartRun;
using GenAIPlatform.Domain.Evaluations;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Evaluations;

internal static class PostgresEvaluationMapping
{
    public static EvaluationRunResult MapRun(
        NpgsqlDataReader reader,
        IReadOnlyList<EvaluationCaseResult> cases)
    {
        return new EvaluationRunResult(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            GetDateTimeOffset(reader, 8),
            reader.IsDBNull(9) ? null : GetDateTimeOffset(reader, 9),
            cases);
    }

    public static DateTimeOffset GetDateTimeOffset(
        NpgsqlDataReader reader,
        int ordinal)
    {
        var value = reader.GetDateTime(ordinal);
        return value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value)
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    public static int ToMilliseconds(TimeSpan value)
    {
        return Math.Max(0, (int)Math.Ceiling(value.TotalMilliseconds));
    }
}
