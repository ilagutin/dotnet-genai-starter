using GenAIPlatform.Application.Knowledge.Embeddings;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Configuration;

internal sealed class EmbeddingProviderOptionsValidator : IValidateOptions<EmbeddingOptions>
{
    public ValidateOptionsResult Validate(string? name, EmbeddingOptions options)
    {
        if (!ProviderKindParser.TryParse(options.Provider, out _))
        {
            return ValidateOptionsResult.Fail(
                $"Embedding provider '{options.Provider}' is unsupported.");
        }

        return MockEmbeddingVariantParser.TryParse(options.MockVariant, out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Mock embedding variant '{options.MockVariant}' is unsupported. Use Hash or Lexical.");
    }
}
