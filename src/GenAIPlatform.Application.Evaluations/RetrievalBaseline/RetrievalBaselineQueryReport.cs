namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// One query's baseline outcome. Only labeled ids, ranks and metrics are reported; the
/// question text, chunk text and vectors never leave the run.
/// </summary>
public sealed record RetrievalBaselineQueryReport(
    string QueryId,
    string Category,
    bool NoMatch,
    IReadOnlyList<string> ExpectedDocumentIds,
    IReadOnlyList<string> RetrievedDocumentIds,
    double? RecallAtK,
    int? FirstRelevantRank,
    bool Hit,
    double ElapsedMilliseconds);
