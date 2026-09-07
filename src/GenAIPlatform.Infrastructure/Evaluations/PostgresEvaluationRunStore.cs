using GenAIPlatform.Application.Evaluations.StartRun;
using Npgsql;

namespace GenAIPlatform.Infrastructure.Evaluations;

internal sealed class PostgresEvaluationRunStore(
    PostgresEvaluationConnectionFactory connectionFactory)
{
    public async Task AddRunAsync(
        EvaluationRunResult run,
        string tenantId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO genai.evaluation_runs (
                run_id, tenant_id, user_id, dataset_version, runner_version, prompt_version, model,
                model_settings, retrieval_configuration, status, started_at_utc, completed_at_utc)
            VALUES (
                @run_id, @tenant_id, @user_id, @dataset_version, @runner_version, @prompt_version, @model,
                @model_settings, @retrieval_configuration, @status, @started_at_utc, @completed_at_utc);
            """, connection);
        PostgresEvaluationParameters.Add(command, "run_id", run.RunId);
        PostgresEvaluationParameters.Add(command, "tenant_id", tenantId);
        PostgresEvaluationParameters.Add(command, "user_id", userId);
        PostgresEvaluationParameters.Add(command, "dataset_version", run.DatasetVersion);
        PostgresEvaluationParameters.Add(command, "runner_version", run.RunnerVersion);
        PostgresEvaluationParameters.Add(command, "prompt_version", run.PromptVersion);
        PostgresEvaluationParameters.Add(command, "model", run.Model);
        PostgresEvaluationParameters.AddJson(command, "model_settings", run.ModelSettings);
        PostgresEvaluationParameters.AddJson(command, "retrieval_configuration", run.RetrievalConfiguration);
        PostgresEvaluationParameters.Add(command, "status", run.Status);
        PostgresEvaluationParameters.Add(command, "started_at_utc", run.StartedAtUtc);
        PostgresEvaluationParameters.Add(command, "completed_at_utc", run.CompletedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteRunAsync(
        Guid runId,
        string status,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE genai.evaluation_runs
            SET status = @status,
                completed_at_utc = @completed_at_utc
            WHERE run_id = @run_id;
            """, connection);
        PostgresEvaluationParameters.Add(command, "run_id", runId);
        PostgresEvaluationParameters.Add(command, "status", status);
        PostgresEvaluationParameters.Add(command, "completed_at_utc", completedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<EvaluationRunResult?> GetRunAsync(
        Guid runId,
        string tenantId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT run_id, dataset_version, runner_version, prompt_version, model,
                   model_settings::text, retrieval_configuration::text, status,
                   started_at_utc, completed_at_utc
            FROM genai.evaluation_runs
            WHERE run_id = @run_id
              AND tenant_id = @tenant_id
              AND user_id = @user_id;
            """, connection);
        PostgresEvaluationParameters.Add(command, "run_id", runId);
        PostgresEvaluationParameters.Add(command, "tenant_id", tenantId);
        PostgresEvaluationParameters.Add(command, "user_id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? PostgresEvaluationMapping.MapRun(reader, [])
            : null;
    }
}
