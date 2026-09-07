using GenAIPlatform.Application.Knowledge.Embeddings;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Configuration;

internal sealed class EmbeddingProviderOptionsValidator : IValidateOptions<EmbeddingOptions>
{
    public ValidateOptionsResult Validate(string? name, EmbeddingOptions options)
    {
        return ProviderKindParser.TryParse(options.Provider, out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Embedding provider '{options.Provider}' is unsupported.");
    }
}
