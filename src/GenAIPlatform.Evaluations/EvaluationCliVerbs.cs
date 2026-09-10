namespace GenAIPlatform.Evaluations;

/// <summary>
/// The verbs the evaluation CLI accepts and the usage text shown for anything else.
/// </summary>
public static class EvaluationCliVerbs
{
    public const string Run = "run";
    public const string RetrievalBaseline = "retrieval-baseline";

    public const string UsageText = """
        Usage: dotnet run --project src/GenAIPlatform.Evaluations -- <verb> [options]

        Verbs:
          run                  Run the embedded evaluation dataset.
          retrieval-baseline   Run the frozen retrieval quality baseline.

        retrieval-baseline options:
          --output <path>      Report file to write (default: retrieval-baseline-report.json).
          --revision <sha>     Code revision recorded in the report (default: GIT_COMMIT or unknown).
        """;

    public static bool IsKnown(string? verb)
    {
        return string.Equals(verb, Run, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(verb, RetrievalBaseline, StringComparison.OrdinalIgnoreCase);
    }
}
