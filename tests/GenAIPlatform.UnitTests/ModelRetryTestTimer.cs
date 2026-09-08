namespace GenAIPlatform.UnitTests;

internal sealed class ModelRetryTestTimer(ModelRetryTestTimeProvider clock, TimerCallback callback, object? state) : ITimer
{
    private readonly object sync = new();
    private DateTimeOffset? dueAt;
    private TimeSpan period;
    private bool disposed;

    public DateTimeOffset? DueAt { get { lock (sync) { return dueAt; } } }

    public bool Change(TimeSpan dueTime, TimeSpan nextPeriod)
    {
        var now = clock.GetUtcNow();
        lock (sync)
        {
            if (disposed) { return false; }
            dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            period = nextPeriod;
        }
        clock.SignalChange();
        return true;
    }

    public void Fire()
    {
        lock (sync)
        {
            if (disposed || dueAt is null) { return; }
            dueAt = period > TimeSpan.Zero ? dueAt + period : null;
        }
        callback(state);
    }

    public void Dispose()
    {
        lock (sync) { disposed = true; dueAt = null; }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
