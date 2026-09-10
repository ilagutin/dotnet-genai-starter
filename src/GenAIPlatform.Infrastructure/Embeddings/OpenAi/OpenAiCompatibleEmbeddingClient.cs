using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiCompatibleEmbeddingClient(
    HttpClient httpClient,
    IOptions<OpenAiCompatibleEmbeddingClientOptions> options)
    : IEmbeddingClient
{
    private readonly OpenAiEmbeddingExecutor executor = new(
        httpClient,
        new OpenAiEmbeddingOptionsResolver(options),
        new OpenAiEmbeddingRequestFactory(),
        new OpenAiEmbeddingResponseMapper(),
        new OpenAiEmbeddingErrorMapper(),
        new OpenAiEmbeddingRetryPolicy());

    public async Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        return await executor.CreateEmbeddingAsync(
            request,
            cancellationToken);
    }
}
