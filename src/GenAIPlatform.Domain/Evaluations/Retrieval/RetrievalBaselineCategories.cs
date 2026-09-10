namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// The closed set of labeled query categories the retrieval baseline understands.
/// </summary>
public static class RetrievalBaselineCategories
{
    public const string Relevant = "relevant";
    public const string Distractor = "distractor";
    public const string Paraphrase = "paraphrase";
    public const string NoMatch = "no-match";
    public const string TenantIsolation = "tenant-isolation";
    public const string PrivateOwnership = "private-ownership";
    public const string DocumentVersion = "document-version";
    public const string EmbeddingCompatibility = "embedding-compatibility";

    public static IReadOnlyList<string> All { get; } =
    [
        Relevant,
        Distractor,
        Paraphrase,
        NoMatch,
        TenantIsolation,
        PrivateOwnership,
        DocumentVersion,
        EmbeddingCompatibility
    ];

    public static bool IsKnown(string? category)
    {
        return category is not null && All.Contains(category, StringComparer.Ordinal);
    }
}
