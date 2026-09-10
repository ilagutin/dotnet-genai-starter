using System.Text.Json;
using GenAIPlatform.Domain.Evaluations;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Evaluations;

internal sealed class PostgresEvaluationCaseStore(
    PostgresEvaluationConnectionFactory connectionFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task AddCaseResultAsync(
        Guid runId,
        EvaluationCaseResult result,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.evaluation_case_results (
                run_id, case_id, name, status, answer, retrieved_count, retrieval_hit,
                latency_ms, estimated_cost, cost_currency, error_code, error_message, checks)
            VALUES (
                @run_id, @case_id, @name, @status, @answer, @retrieved_count, @retrieval_hit,
                @latency_ms, @estimated_cost, @cost_currency, @error_code, @error_message, @checks);
            """, connection);
        PostgresEvaluationParameters.Add(command, "run_id", runId);
        PostgresEvaluationParameters.Add(command, "case_id", result.CaseId);
        PostgresEvaluationParameters.Add(command, "name", result.Name);
        PostgresEvaluationParameters.Add(command, "status", result.Status);
        PostgresEvaluationParameters.Add(command, "answer", result.Answer);
        PostgresEvaluationParameters.Add(command, "retrieved_count", result.RetrievedCount);
        PostgresEvaluationParameters.Add(command, "retrieval_hit", result.RetrievalHit);
        PostgresEvaluationParameters.Add(command, "latency_ms", PostgresEvaluationMapping.ToMilliseconds(result.Latency));
        PostgresEvaluationParameters.Add(command, "estimated_cost", result.EstimatedCost);
        PostgresEvaluationParameters.Add(command, "cost_currency", result.CostCurrency);
        PostgresEvaluationParameters.Add(command, "error_code", result.ErrorCode);
        PostgresEvaluationParameters.Add(command, "error_message", result.ErrorMessage);
        PostgresEvaluationParameters.AddJson(command, "checks", JsonSerializer.Serialize(result.Checks, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EvaluationCaseResult>> ReadCasesAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT case_id, name, status, answer, retrieved_count, retrieval_hit,
                   latency_ms, estimated_cost, cost_currency, error_code, error_message, checks::text
            FROM genai.evaluation_case_results
            WHERE run_id = @run_id
            ORDER BY case_id;
            """, connection);
        PostgresEvaluationParameters.Add(command, "run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<EvaluationCaseResult>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapCase(reader));
        }

        return results;
    }

    private static EvaluationCaseResult MapCase(NpgsqlDataReader reader)
    {
        return new EvaluationCaseResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetBoolean(5),
            TimeSpan.FromMilliseconds(reader.GetInt32(6)),
            reader.GetDecimal(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            JsonSerializer.Deserialize<IReadOnlyList<EvaluationCheckResult>>(
                reader.GetString(11),
                JsonOptions) ?? []);
    }
}
