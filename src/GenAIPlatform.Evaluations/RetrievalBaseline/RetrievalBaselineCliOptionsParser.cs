namespace GenAIPlatform.Evaluations.RetrievalBaseline;

/// <summary>
/// Parses the <c>retrieval-baseline</c> verb's own options. Configuration overrides such
/// as <c>--GenAIPlatform:Embeddings:MockVariant=Lexical</c> are left for the host
/// configuration pipeline and are not treated as usage errors here.
/// </summary>
public static class RetrievalBaselineCliOptionsParser
{
    public const string OutputOption = "--output";
    public const string RevisionOption = "--revision";
    public const string DefaultOutputPath = "retrieval-baseline-report.json";
    public const string RevisionEnvironmentVariable = "GIT_COMMIT";
    private const string UnknownRevision = "unknown";

    public static RetrievalBaselineCliOptionsResult Parse(
        IReadOnlyList<string> args,
        string? revisionFromEnvironment)
    {
        string? outputPath = null;
        string? revision = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (!IsOwnOption(argument))
            {
                continue;
            }

            if (index + 1 >= args.Count || IsOption(args[index + 1]))
            {
                return RetrievalBaselineCliOptionsResult.Failed($"Option {argument} requires a value.");
            }

            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value))
            {
                return RetrievalBaselineCliOptionsResult.Failed($"Option {argument} requires a value.");
            }

            if (IsOption(argument, OutputOption))
            {
                outputPath = value.Trim();
            }
            else
            {
                revision = value.Trim();
            }
        }

        return RetrievalBaselineCliOptionsResult.Parsed(new RetrievalBaselineCliOptions(
            outputPath ?? DefaultOutputPath,
            ResolveRevision(revision, revisionFromEnvironment)));
    }

    public static bool IsOwnOption(string argument)
    {
        return IsOption(argument, OutputOption) || IsOption(argument, RevisionOption);
    }

    private static string ResolveRevision(string? revision, string? revisionFromEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(revision))
        {
            return revision;
        }

        return string.IsNullOrWhiteSpace(revisionFromEnvironment)
            ? UnknownRevision
            : revisionFromEnvironment.Trim();
    }

    private static bool IsOption(string argument, string option)
    {
        return string.Equals(argument, option, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOption(string argument)
    {
        return argument.StartsWith('-');
    }
}
