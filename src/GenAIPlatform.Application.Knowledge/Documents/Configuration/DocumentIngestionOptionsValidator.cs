using GenAIPlatform.Application.Knowledge.Embeddings;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents;

internal sealed class DocumentIngestionOptionsValidator(IOptions<EmbeddingOptions> embeddingOptions)
    : IValidateOptions<DocumentIngestionOptions>
{
    public ValidateOptionsResult Validate(string? name, DocumentIngestionOptions options)
    {
        var valid =
            options.MaxUploadBytes > 0 &&
            options.AllowedExtensions is not null &&
            options.AllowedExtensions.Count > 0 &&
            options.AllowedExtensions.All(IsSupportedDocumentExtension) &&
            options.ChunkMaxCharacters > 0 &&
            options.ChunkMaxCharacters <= embeddingOptions.Value.MaxInputCharacters &&
            options.ChunkOverlapCharacters >= 0 &&
            options.ChunkOverlapCharacters < options.ChunkMaxCharacters &&
            !string.IsNullOrWhiteSpace(options.ChunkingProfile) &&
            !string.IsNullOrWhiteSpace(options.ChunkingProfileVersion) &&
            options.MaxIndexingAttempts > 0 &&
            options.IndexingRetryDelaySeconds >= 0 &&
            options.MaxStorageCleanupAttempts > 0 &&
            options.StorageCleanupRetryDelaySeconds >= 0 &&
            options.MaxIndexingJobsPerPoll is > 0 and <= 50 &&
            options.MaxStorageCleanupRequestsPerPoll is > 0 and <= 50 &&
            options.ProcessingJobLeaseSeconds > 0 &&
            options.WorkerPollIntervalSeconds > 0;

        return valid
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Document ingestion configuration is invalid.");
    }

    private static bool IsSupportedDocumentExtension(string? extension)
    {
        return extension is { Length: > 1 } && extension[0] == '.' &&
            extension.Skip(1).All(char.IsAsciiLetterOrDigit);
    }
}
