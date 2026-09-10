namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// Canonicalizes the configured mock embedding variant for the baseline report and its
/// settings hash. The Infrastructure parser that selects the adapter is internal to that
/// assembly, so the same spelling rules are restated here: trim, ignore separators and
/// case, and map the known values onto one canonical name. Two runs that configured
/// "lexical" and "Lexical" measured the same thing and must produce the same settings
/// hash; an unrecognized value is reported as written, because host options validation has
/// already rejected it before a run can reach this code.
/// </summary>
internal static class MockEmbeddingVariantCanonicalizer
{
    public const string Hash = "Hash";
    public const string Lexical = "Lexical";

    public static string Canonicalize(string? variant)
    {
        var trimmed = variant?.Trim() ?? string.Empty;

        return Normalize(trimmed) switch
        {
            "HASH" => Hash,
            "LEXICAL" => Lexical,
            _ => trimmed
        };
    }

    private static string Normalize(string variant)
    {
        return variant
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }
}
