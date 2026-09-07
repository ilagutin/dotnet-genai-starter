using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.ModelGateway.OpenAi;

internal sealed class OpenAiCompatibleModelClient(
    HttpClient httpClient,
    IOptions<OpenAiCompatibleModelClientOptions> options)
    : IAiModelClient
{
    private readonly OpenAiModelCompletionExecutor executor = new(
        httpClient,
        new OpenAiModelOptionsResolver(options),
        new OpenAiModelRequestFactory(),
        new OpenAiModelResponseMapper(),
        new OpenAiModelErrorMapper(),
        new OpenAiModelRetryPolicy());

    public async Task<AiModelResponse> CompleteAsync(
        AiModelRequest request,
        CancellationToken cancellationToken)
    {
        return await executor.CompleteAsync(
            request,
            cancellationToken);
    }
}
