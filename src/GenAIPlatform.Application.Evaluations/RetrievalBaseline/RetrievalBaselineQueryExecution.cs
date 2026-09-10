namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// What one baseline query retrieved, as dataset document ids in retrieval order.
/// </summary>
public sealed record RetrievalBaselineQueryExecution(
    IReadOnlyList<string> RetrievedDocumentIds,
    double ElapsedMilliseconds);
