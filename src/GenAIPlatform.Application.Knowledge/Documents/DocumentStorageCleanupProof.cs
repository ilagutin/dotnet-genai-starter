namespace GenAIPlatform.Application.Knowledge.Documents;

internal static class DocumentStorageCleanupProof
{
    public const string RepositoryCreateNotStarted = "repository-create-not-started";
    public const string StorageNotCommitted = "storage-not-committed";
    public const string MetadataNotCommitted = nameof(DocumentMetadataNotCommittedException);

    public static bool IsValid(string metadataAbsenceProof)
    {
        return metadataAbsenceProof is
            RepositoryCreateNotStarted or
            StorageNotCommitted or
            MetadataNotCommitted;
    }
}
