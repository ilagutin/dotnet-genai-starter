namespace GenAIPlatform.Infrastructure.Documents.Local;

internal static class LocalDocumentStoragePathResolver
{
    public static string ResolveRootPath(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (Path.IsPathFullyQualified(rootPath))
        {
            return Path.GetFullPath(rootPath);
        }

        if (Path.IsPathRooted(rootPath))
        {
            throw new InvalidOperationException(
                $"Document storage root '{rootPath}' must be fully qualified or relative.");
        }

        return Path.GetFullPath(rootPath, AppContext.BaseDirectory);
    }

    public static bool CanResolveRootPath(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            _ = ResolveRootPath(rootPath);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
