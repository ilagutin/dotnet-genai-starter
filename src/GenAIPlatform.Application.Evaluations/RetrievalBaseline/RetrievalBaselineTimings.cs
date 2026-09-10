namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// Wall-clock cost of a baseline run. Timings are reported separately from quality
/// because they vary with the machine and must never be read as a quality signal.
/// </summary>
public sealed record RetrievalBaselineTimings(
    double TotalMilliseconds,
    double CorpusReplaceMilliseconds,
    double QueryMeanMilliseconds,
    double QueryMaxMilliseconds);
