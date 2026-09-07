using GenAIPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Documents.Local;

internal sealed class LocalDocumentPathPolicy(IOptions<LocalDocumentStorageOptions> options)
{
    public string GetRootPath()
    {
        return LocalDocumentStoragePathResolver.ResolveRootPath(options.Value.RootPath);
    }

    public string ResolvePathWithinRoot(string storagePath)
    {
        var rootPath = GetRootPath();
        var fullPath = Path.GetFullPath(
            Path.IsPathFullyQualified(storagePath)
                ? storagePath
                : Path.Combine(rootPath, storagePath));
        var rootWithSeparator = EnsureTrailingDirectorySeparator(rootPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(rootWithSeparator, comparison))
        {
            throw new UnauthorizedAccessException(
                "Document storage path is outside the configured document storage root.");
        }

        return fullPath;
    }

    public string GetStorageKey(
        Guid documentId,
        string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return $"{documentId:n}{extension}";
    }

    public string GetStagingKey(string storagePath)
    {
        var fileName = Path.GetFileName(storagePath);
        return Path.Combine(".staging", fileName);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return Path.EndsInDirectorySeparator(path)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
