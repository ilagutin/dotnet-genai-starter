namespace GenAIPlatform.Infrastructure.Documents.Local;

internal sealed class LocalDocumentDeletion(
    LocalDocumentPathPolicy pathPolicy,
    LocalDocumentFileOperations fileOps)
{
    public Task DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        var resolvedStoragePath = pathPolicy.ResolvePathWithinRoot(storagePath);
        fileOps.DeleteFileIfExists(resolvedStoragePath);
        fileOps.DeleteFileIfExists(pathPolicy.ResolvePathWithinRoot(pathPolicy.GetStagingKey(storagePath)));
        return Task.CompletedTask;
    }
}
