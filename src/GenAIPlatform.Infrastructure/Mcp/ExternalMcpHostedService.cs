using Microsoft.Extensions.Hosting;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed class ExternalMcpHostedService(ExternalMcpConnectionManager manager)
    : IHostedService, IAsyncDisposable
{
    private readonly object gate = new();
    private Task? shutdown;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        manager.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GetShutdownTask().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await GetShutdownTask();
    }

    private Task GetShutdownTask()
    {
        lock (gate)
        {
            return shutdown ??= manager.ShutdownAsync();
        }
    }
}
