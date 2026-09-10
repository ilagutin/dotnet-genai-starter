namespace GenAIPlatform.Domain.Evaluations.Checks;

public sealed class EvaluationCheckRunner
{
    public IReadOnlyList<EvaluationCheckResult> Run(
        EvaluationCase evaluationCase,
        string answer,
        int retrievedCount)
    {
        return evaluationCase.Checks
            .Select(check => RunCheck(check, answer, retrievedCount))
            .ToArray();
    }

    private static EvaluationCheckResult RunCheck(
        EvaluationCheck check,
        string answer,
        int retrievedCount)
    {
        return NormalizeType(check.Type) switch
        {
            "retrieval" => CheckRetrieval(check, retrievedCount),
            "citation" => CheckCitation(check, answer),
            "required_phrase" => CheckRequiredPhrase(check, answer),
            "forbidden_phrase" => CheckForbiddenPhrase(check, answer),
            _ => new EvaluationCheckResult(check.Type, false, $"Unknown check type '{check.Type}'.")
        };
    }

    private static EvaluationCheckResult CheckRetrieval(
        EvaluationCheck check,
        int retrievedCount)
    {
        var minimumHits = Math.Max(1, check.MinimumHits ?? 1);
        var passed = retrievedCount >= minimumHits;
        return new EvaluationCheckResult(
            "retrieval",
            passed,
            passed
                ? $"Retrieved {retrievedCount} chunk(s)."
                : $"Expected at least {minimumHits} retrieved chunk(s), got {retrievedCount}.");
    }

    private static EvaluationCheckResult CheckCitation(
        EvaluationCheck check,
        string answer)
    {
        var references = check.CitationReferences is { Count: > 0 }
            ? check.CitationReferences
            : ["[1]"];
        var missing = references
            .Where(reference => !ContainsOrdinalIgnoreCase(answer, reference))
            .ToArray();

        return new EvaluationCheckResult(
            "citation",
            missing.Length == 0,
            missing.Length == 0
                ? "Required citation references were present."
                : $"Missing citation reference(s): {string.Join(", ", missing)}.");
    }

    private static EvaluationCheckResult CheckRequiredPhrase(
        EvaluationCheck check,
        string answer)
    {
        var phrase = check.Phrase ?? string.Empty;
        var passed = !string.IsNullOrWhiteSpace(phrase) &&
                     ContainsOrdinalIgnoreCase(answer, phrase);
        return new EvaluationCheckResult(
            "required_phrase",
            passed,
            passed
                ? $"Required phrase '{phrase}' was present."
                : $"Required phrase '{phrase}' was missing.");
    }

    private static EvaluationCheckResult CheckForbiddenPhrase(
        EvaluationCheck check,
        string answer)
    {
        var phrase = check.Phrase ?? string.Empty;
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return new EvaluationCheckResult(
                "forbidden_phrase",
                false,
                "Forbidden phrase check requires a non-empty phrase.");
        }

        var passed = !ContainsOrdinalIgnoreCase(answer, phrase);
        return new EvaluationCheckResult(
            "forbidden_phrase",
            passed,
            passed
                ? $"Forbidden phrase '{phrase}' was absent."
                : $"Forbidden phrase '{phrase}' was present.");
    }

    private static string NormalizeType(string value)
    {
        return value.Trim().ToLowerInvariant().Replace('-', '_');
    }

    private static bool ContainsOrdinalIgnoreCase(string value, string expected)
    {
        return value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }
}
