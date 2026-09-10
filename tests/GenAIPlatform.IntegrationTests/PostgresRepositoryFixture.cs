using Testcontainers.PostgreSql;

namespace GenAIPlatform.IntegrationTests;

public sealed class PostgresRepositoryFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private bool started;

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (started)
        {
            await container!.DisposeAsync();
        }
    }

    public async Task<string> GetConnectionStringAsync()
    {
        if (!started)
        {
            container ??= new PostgreSqlBuilder("pgvector/pgvector:pg16")
                .WithDatabase("genai_platform_tests")
                .WithUsername("genai")
                .WithPassword("genai_dev_password")
                .Build();
            await container.StartAsync();
            started = true;
        }

        return container!.GetConnectionString();
    }
}
