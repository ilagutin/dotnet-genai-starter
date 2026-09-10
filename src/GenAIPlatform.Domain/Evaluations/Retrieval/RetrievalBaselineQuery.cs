namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// A labeled benchmark query. <paramref name="ExpectedDocumentIds" /> holds the document
/// ids that must be retrieved for the query's caller; a query with
/// <paramref name="NoMatch" /> set must retrieve nothing at all.
/// </summary>
public sealed record RetrievalBaselineQuery(
    string Id,
    string Question,
    string TenantId,
    string UserId,
    string Category,
    IReadOnlyList<string> ExpectedDocumentIds,
    bool NoMatch = false,
    IReadOnlyList<string>? ExpectedChunkIds = null);
