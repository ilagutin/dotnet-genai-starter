using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Evaluations.StartRun;
using GenAIPlatform.Domain.Evaluations;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresEvaluationRunRepositoryTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task EvaluationPersistence_StoresRunResultsAndReturnsSummary()
    {
        using var scope = await CreateScopeAsync();
        await CleanEvaluationTablesAsync(scope.ConnectionString);
        var repository = scope.Services.GetRequiredService<IEvaluationRunRepository>();
        var runId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var run = new EvaluationRunResult(
            runId,
            "sample-v1",
            "runner-v1",
            "v1",
            "mock-chat-evaluation",
            "{\"temperature\":0}",
            "{\"topK\":5}",
            "Running",
            DateTimeOffset.Parse("2026-05-15T12:00:00Z"),
            CompletedAtUtc: null,
            Cases: []);

        await repository.AddRunAsync(run, "tenant-a", "alice", TestContext.Current.CancellationToken);
        await repository.AddCaseResultAsync(
            runId,
            new EvaluationCaseResult(
                "case-1",
                "Case 1",
                "Passed",
                "answer",
                RetrievedCount: 1,
                RetrievalHit: true,
                TimeSpan.FromMilliseconds(25),
                EstimatedCost: 0.001m,
                CostCurrency: "USD",
                ErrorCode: null,
                ErrorMessage: null,
                [new EvaluationCheckResult("required_phrase", true, "ok")]),
            TestContext.Current.CancellationToken);
        await repository.AddCaseResultAsync(
            runId,
            new EvaluationCaseResult(
                "case-2",
                "Case 2",
                "Failed",
                Answer: null,
                RetrievedCount: 0,
                RetrievalHit: false,
                TimeSpan.FromMilliseconds(75),
                EstimatedCost: 0,
                CostCurrency: "USD",
                ErrorCode: "provider_unavailable",
                ErrorMessage: "failed",
                [new EvaluationCheckResult("runtime", false, "failed")]),
            TestContext.Current.CancellationToken);
        await repository.CompleteRunAsync(
            runId,
            "Failed",
            DateTimeOffset.Parse("2026-05-15T12:01:00Z"),
            TestContext.Current.CancellationToken);

        var persisted = await repository.GetRunAsync(runId, "tenant-a", "alice", TestContext.Current.CancellationToken);
        var summary = await repository.GetSummaryAsync(runId, "tenant-a", "alice", TestContext.Current.CancellationToken);

        Assert.NotNull(persisted);
        Assert.Equal("Failed", persisted.Status);
        Assert.Equal("sample-v1", persisted.DatasetVersion);
        Assert.Equal(2, persisted.Cases.Count);
        Assert.NotNull(summary);
        Assert.Equal(2, summary.TotalCases);
        Assert.Equal(1, summary.PassedCases);
        Assert.Equal(1, summary.FailedCaseCount);
        Assert.Equal(0.5, summary.RetrievalHitRate);
        Assert.Equal(50, summary.AverageLatencyMs);
        Assert.Equal(0.0005m, summary.AverageCost);
        Assert.Single(summary.FailedCases);
    }

    [DockerAvailableFact]
    public async Task EvaluationPersistence_FiltersRunReadsByTenant()
    {
        using var scope = await CreateScopeAsync();
        await CleanEvaluationTablesAsync(scope.ConnectionString);
        var repository = scope.Services.GetRequiredService<IEvaluationRunRepository>();
        var runId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var run = new EvaluationRunResult(
            runId,
            "sample-v1",
            "runner-v1",
            "v1",
            "mock-chat-evaluation",
            "{\"temperature\":0}",
            "{\"topK\":5}",
            "Running",
            DateTimeOffset.Parse("2026-05-15T12:00:00Z"),
            CompletedAtUtc: null,
            Cases: []);

        await repository.AddRunAsync(run, "tenant-a", "alice", TestContext.Current.CancellationToken);

        var sameTenantRun = await repository.GetRunAsync(runId, "tenant-a", "alice", TestContext.Current.CancellationToken);
        var otherUserRun = await repository.GetRunAsync(runId, "tenant-a", "bob", TestContext.Current.CancellationToken);
        var otherTenantRun = await repository.GetRunAsync(runId, "tenant-b", "alice", TestContext.Current.CancellationToken);
        var otherTenantSummary = await repository.GetSummaryAsync(runId, "tenant-b", "alice", TestContext.Current.CancellationToken);

        Assert.NotNull(sameTenantRun);
        Assert.Null(otherUserRun);
        Assert.Null(otherTenantRun);
        Assert.Null(otherTenantSummary);
    }

    private async Task<RepositoryScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = connectionString,
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        var serviceProvider = services.BuildServiceProvider();

        return new RepositoryScope(serviceProvider, connectionString);
    }

    private static async Task CleanEvaluationTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            TRUNCATE TABLE
                genai.evaluation_case_results,
                genai.evaluation_runs;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record RepositoryScope(
        ServiceProvider Services,
        string ConnectionString)
        : IDisposable
    {
        public void Dispose()
        {
            Services.Dispose();
        }
    }
}
