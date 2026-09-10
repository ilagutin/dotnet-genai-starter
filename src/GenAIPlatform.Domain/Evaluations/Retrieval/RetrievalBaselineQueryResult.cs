namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// The scored outcome of one baseline query. <paramref name="RecallAtK" /> and
/// <paramref name="FirstRelevantRank" /> are null for queries that label no relevant
/// document, because those queries are excluded from the recall and rank denominators.
/// </summary>
public sealed record RetrievalBaselineQueryResult(
    string QueryId,
    string Category,
    bool NoMatch,
    IReadOnlyList<string> ExpectedDocumentIds,
    IReadOnlyList<string> RetrievedDocumentIds,
    double? RecallAtK,
    int? FirstRelevantRank,
    double ReciprocalRank,
    bool Hit);
