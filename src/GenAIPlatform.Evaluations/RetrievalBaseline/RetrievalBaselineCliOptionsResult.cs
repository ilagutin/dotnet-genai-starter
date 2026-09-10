namespace GenAIPlatform.Evaluations.RetrievalBaseline;

/// <summary>
/// The outcome of parsing <c>retrieval-baseline</c> arguments. <see cref="Error" /> is set
/// exactly when <see cref="Options" /> is null.
/// </summary>
public sealed record RetrievalBaselineCliOptionsResult(
    RetrievalBaselineCliOptions? Options,
    string? Error)
{
    public static RetrievalBaselineCliOptionsResult Parsed(RetrievalBaselineCliOptions options)
    {
        return new RetrievalBaselineCliOptionsResult(options, Error: null);
    }

    public static RetrievalBaselineCliOptionsResult Failed(string error)
    {
        return new RetrievalBaselineCliOptionsResult(Options: null, error);
    }
}
