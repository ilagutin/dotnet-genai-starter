using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Documents.Local;

internal sealed class LocalDocumentStorage : IDocumentStorage
{
    private readonly LocalDocumentFileWriter fileWriter;
    private readonly LocalDocumentCommitter committer;
    private readonly LocalDocumentReader reader;
    private readonly LocalDocumentDeletion deletion;

    public LocalDocumentStorage(IOptions<LocalDocumentStorageOptions> options)
    {
        var pathPolicy = new LocalDocumentPathPolicy(options);
        var fileOps = new LocalDocumentFileOperations();

        fileWriter = new LocalDocumentFileWriter(pathPolicy, fileOps);
        committer = new LocalDocumentCommitter(pathPolicy, fileOps);
        reader = new LocalDocumentReader(pathPolicy);
        deletion = new LocalDocumentDeletion(pathPolicy, fileOps);
    }

    public async Task<StoredDocument> SaveAsync(
        Guid documentId,
        string fileName,
        Stream content,
        long maxSizeBytes,
        CancellationToken cancellationToken)
    {
        return await fileWriter.SaveAsync(
            documentId,
            fileName,
            content,
            maxSizeBytes,
            cancellationToken);
    }

    public Task CommitAsync(
        StoredDocument document,
        CancellationToken cancellationToken)
    {
        return committer.CommitAsync(document, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(
        string storagePath,
        CancellationToken cancellationToken)
    {
        return reader.OpenReadAsync(storagePath, cancellationToken);
    }

    public Task DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken)
    {
        return deletion.DeleteAsync(storagePath, cancellationToken);
    }
}
