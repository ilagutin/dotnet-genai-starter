using GenAIPlatform.Application.Evaluations.RetrievalBaseline;

namespace GenAIPlatform.UnitTests;

public sealed class MockEmbeddingVariantCanonicalizerTests
{
    [Theory]
    [InlineData("Lexical")]
    [InlineData("lexical")]
    [InlineData("LEXICAL")]
    [InlineData("  Lexical  ")]
    [InlineData("lexi_cal")]
    [InlineData("lexi-cal")]
    public void EverySpellingOfTheSameVariantCanonicalizesToOneName(string configured)
    {
        Assert.Equal(MockEmbeddingVariantCanonicalizer.Lexical, MockEmbeddingVariantCanonicalizer.Canonicalize(configured));
    }

    [Theory]
    [InlineData("Hash")]
    [InlineData("hash")]
    [InlineData(" HASH ")]
    public void TheHashVariantCanonicalizesToOneName(string configured)
    {
        Assert.Equal(MockEmbeddingVariantCanonicalizer.Hash, MockEmbeddingVariantCanonicalizer.Canonicalize(configured));
    }

    [Fact]
    public void SpellingDifferencesDoNotChangeTheSettingsHash()
    {
        var hashes = new[] { "Lexical", "lexical", "LEXICAL", " lexical " }
            .Select(variant => RetrievalBaselineSettingsHasher.Hash(
                "mock-lexical",
                "mock-embedding",
                MockEmbeddingVariantCanonicalizer.Canonicalize(variant),
                1024,
                3,
                0.2))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Single(hashes);
        Assert.NotEqual(
            hashes[0],
            RetrievalBaselineSettingsHasher.Hash("mock-lexical", "mock-embedding", "Hash", 1024, 3, 0.2));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("Semantic", "Semantic")]
    public void AnUnrecognizedVariantIsReportedAsWritten(string? configured, string expected)
    {
        Assert.Equal(expected, MockEmbeddingVariantCanonicalizer.Canonicalize(configured));
    }
}
