namespace GenAIPlatform.Evaluations.RetrievalBaseline;

/// <summary>
/// The resolved options of one <c>retrieval-baseline</c> invocation.
/// </summary>
public sealed record RetrievalBaselineCliOptions(
    string OutputPath,
    string CodeRevision);
