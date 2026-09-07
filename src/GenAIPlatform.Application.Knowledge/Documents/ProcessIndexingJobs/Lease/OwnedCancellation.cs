namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Lease;

internal sealed class OwnedCancellation : IDisposable
{
    private CancellationTokenSource? source;

    private OwnedCancellation(CancellationTokenSource source)
    {
        this.source = source;
    }

    public CancellationToken Token =>
        source?.Token ?? throw new InvalidOperationException("Cancellation ownership has been transferred.");

    public static OwnedCancellation CreateLinked(CancellationToken cancellationToken)
    {
        return new OwnedCancellation(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
    }

    public CancellationTokenSource Transfer()
    {
        var current = source ?? throw new InvalidOperationException("Cancellation ownership has already been transferred.");
        source = null;

        return current;
    }

    public void Dispose()
    {
        source?.Dispose();
        source = null;
    }
}
