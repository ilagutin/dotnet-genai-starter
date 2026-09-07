using GenAIPlatform.Infrastructure.Configuration;

namespace GenAIPlatform.Infrastructure.Documents.Local;

internal static class LocalDocumentStoragePathResolver
{
    private const string RepositoryMarkerFileName = "GenAIPlatform.slnx";

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

        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        if (repositoryRoot is null)
        {
            throw new InvalidOperationException(
                $"Relative document storage root '{rootPath}' cannot be resolved safely. " +
                $"Configure {LocalDocumentStorageOptions.SectionName}:RootPath as an absolute shared path for API and Worker, " +
                $"or use the local starter-kit fallback from the repository layout containing {RepositoryMarkerFileName}.");
        }

        return Path.GetFullPath(rootPath, repositoryRoot);
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

    private static string? FindRepositoryRoot(string startPath)
    {
        var directory = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : Directory.GetParent(startPath);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RepositoryMarkerFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
