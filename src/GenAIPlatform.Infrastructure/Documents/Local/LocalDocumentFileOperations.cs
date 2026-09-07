namespace GenAIPlatform.Infrastructure.Documents.Local;

internal sealed class LocalDocumentFileOperations
{
    public void DeleteFileIfExists(string storagePath)
    {
        if (File.Exists(storagePath))
        {
            File.Delete(storagePath);
        }
    }

    public void PromoteStagedFile(
        string stagedStoragePath,
        string storagePath)
    {
        try
        {
            File.Move(stagedStoragePath, storagePath);
        }
        catch (IOException) when (File.Exists(storagePath))
        {
            DeleteFileIfExists(stagedStoragePath);
        }
    }
}
