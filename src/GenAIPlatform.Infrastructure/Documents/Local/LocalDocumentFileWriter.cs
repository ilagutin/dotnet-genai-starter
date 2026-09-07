using System.Buffers;
using System.Security.Cryptography;
using GenAIPlatform.Application.Knowledge.Documents;

namespace GenAIPlatform.Infrastructure.Documents.Local;

internal sealed class LocalDocumentFileWriter(
    LocalDocumentPathPolicy pathPolicy,
    LocalDocumentFileOperations fileOps)
{
    public async Task<StoredDocument> SaveAsync(
        Guid documentId,
        string fileName,
        Stream content,
        long maxSizeBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var rootPath = pathPolicy.GetRootPath();
        var storageKey = pathPolicy.GetStorageKey(documentId, fileName);
        var stagingKey = pathPolicy.GetStagingKey(storageKey);
        var stagingPath = pathPolicy.ResolvePathWithinRoot(stagingKey);
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);

        using var sha256 = SHA256.Create();
        var sizeBytes = await WriteStagedFileAsync(
            content,
            stagingPath,
            maxSizeBytes,
            sha256,
            cancellationToken);

        return new StoredDocument(
            storageKey,
            Convert.ToHexString(sha256.Hash ?? []).ToLowerInvariant(),
            sizeBytes,
            stagingKey);
    }

    private async Task<long> WriteStagedFileAsync(
        Stream content,
        string stagingPath,
        long maxSizeBytes,
        HashAlgorithm sha256,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long sizeBytes = 0;

        try
        {
            await using var target = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                var nextSize = sizeBytes + read;
                if (maxSizeBytes > 0 && nextSize > maxSizeBytes)
                {
                    throw new DocumentStorageLimitExceededException(maxSizeBytes);
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha256.TransformBlock(buffer, 0, read, null, 0);
                sizeBytes = nextSize;
            }

            sha256.TransformFinalBlock([], 0, 0);
            return sizeBytes;
        }
        catch
        {
            fileOps.DeleteFileIfExists(stagingPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
