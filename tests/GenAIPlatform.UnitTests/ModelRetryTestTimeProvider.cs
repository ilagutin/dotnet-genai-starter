namespace GenAIPlatform.UnitTests;

internal sealed class ModelRetryTestTimeProvider : TimeProvider
{
    private readonly object sync = new();
    private readonly List<ModelRetryTestTimer> timers = [];
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (sync) { return now; }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ModelRetryTestTimer(this, callback, state);
        lock (sync) { timers.Add(timer); }
        timer.Change(dueTime, period);
        return timer;
    }

    public void SignalChange()
    {
        lock (sync)
        {
            changed.TrySetResult();
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task WaitForDelayAsync(TimeSpan delay)
    {
        while (true)
        {
            Task pending;
            lock (sync)
            {
                if (timers.Any(timer => timer.DueAt == now + delay)) { return; }
                pending = changed.Task;
            }
            await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    public void Advance(TimeSpan duration)
    {
        ModelRetryTestTimer[] due;
        lock (sync)
        {
            now += duration;
            due = timers.Where(timer => timer.DueAt <= now).ToArray();
        }
        foreach (var timer in due) { timer.Fire(); }
    }
}
