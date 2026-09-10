namespace GenAIPlatform.Infrastructure.Configuration;

internal static class MockEmbeddingVariantParser
{
    public static bool TryParse(string? variant, out MockEmbeddingVariant kind)
    {
        switch (Normalize(variant))
        {
            case "HASH":
                kind = MockEmbeddingVariant.Hash;
                return true;
            case "LEXICAL":
                kind = MockEmbeddingVariant.Lexical;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string Normalize(string? variant)
    {
        return variant?
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant() ?? string.Empty;
    }
}
