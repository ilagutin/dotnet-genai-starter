using System.ComponentModel.DataAnnotations;
using GenAIPlatform.Application.Core.Configuration;

namespace GenAIPlatform.Application.Knowledge.Embeddings;

public sealed class EmbeddingOptions
{
    public const string SectionName = "GenAIPlatform:Embeddings";

    [RequiredNonBlank]
    public string Provider { get; init; } = "Mock";

    [RequiredNonBlank]
    public string DefaultModel { get; init; } = "mock-embedding";

    /// <summary>
    /// Selects which deterministic mock embedding shape the Mock provider uses.
    /// "Hash" is the default content-hash mock; "Lexical" is the token-overlap mock the
    /// retrieval baseline uses. Neither variant carries semantic meaning.
    /// </summary>
    [RequiredNonBlank]
    public string MockVariant { get; init; } = "Hash";

    [Range(1, 4096)]
    public int MockDimensions { get; init; } = 16;

    [Range(1, int.MaxValue)]
    public int MaxInputCharacters { get; init; } = 8000;
}
