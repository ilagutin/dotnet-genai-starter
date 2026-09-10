namespace GenAIPlatform.Application.Knowledge.Documents;

public sealed class DocumentIngestionOptions
{
    public const string SectionName = "GenAIPlatform:DocumentIngestion";

    public long MaxUploadBytes { get; init; } = 2 * 1024 * 1024;

    private IReadOnlyCollection<string> allowedExtensions = [".txt", ".md"];

    public IReadOnlyCollection<string> AllowedExtensions
    {
        get => allowedExtensions;
        init => allowedExtensions = value?.Select(static extension => extension?.Trim().ToLowerInvariant() ?? string.Empty).ToArray() ?? [];
    }

    internal bool AllowsExtension(string extension) =>
        AllowedExtensions.Contains(extension.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    public int ChunkMaxCharacters { get; init; } = 1200;

    public int ChunkOverlapCharacters { get; init; } = 150;

    public string ChunkingProfile { get; init; } = "plain-text";

    public string ChunkingProfileVersion { get; init; } = "v1";

    public int MaxIndexingAttempts { get; init; } = 3;

    public int IndexingRetryDelaySeconds { get; init; } = 30;

    public int MaxStorageCleanupAttempts { get; init; } = 3;

    public int StorageCleanupRetryDelaySeconds { get; init; } = 30;

    public int MaxIndexingJobsPerPoll { get; init; } = 5;

    public int MaxStorageCleanupRequestsPerPoll { get; init; } = 5;

    public int ProcessingJobLeaseSeconds { get; init; } = 900;

    public int WorkerPollIntervalSeconds { get; init; } = 30;
}
