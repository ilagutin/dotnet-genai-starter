using GenAIPlatform.Application.Knowledge.Documents;

namespace GenAIPlatform.Infrastructure.Documents.Local;

internal sealed class LocalDocumentCommitter(
    LocalDocumentPathPolicy pathPolicy,
    LocalDocumentFileOperations fileOps)
{
    public Task CommitAsync(
        StoredDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(document.StagedStoragePath))
        {
            throw new InvalidOperationException(
                "A staged storage identity is required to commit a document.");
        }

        var storagePath = pathPolicy.ResolvePathWithinRoot(document.StoragePath);
        var stagedStoragePath = pathPolicy.ResolvePathWithinRoot(document.StagedStoragePath);
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);

        if (File.Exists(storagePath))
        {
            fileOps.DeleteFileIfExists(stagedStoragePath);
            return Task.CompletedTask;
        }

        if (!File.Exists(stagedStoragePath))
        {
            throw new FileNotFoundException(
                "The staged document file was not found.",
                stagedStoragePath);
        }

        fileOps.PromoteStagedFile(stagedStoragePath, storagePath);
        return Task.CompletedTask;
    }
}
