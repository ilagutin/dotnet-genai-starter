namespace GenAIPlatform.Infrastructure.Documents.Local;

internal sealed class LocalDocumentReader(LocalDocumentPathPolicy pathPolicy)
{
    public Task<Stream> OpenReadAsync(
        string storagePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        var readablePath = pathPolicy.ResolvePathWithinRoot(storagePath);
        if (!File.Exists(readablePath))
        {
            throw new FileNotFoundException(
                "The committed document file was not found.",
                readablePath);
        }

        Stream stream = new FileStream(
            readablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return Task.FromResult(stream);
    }
}
