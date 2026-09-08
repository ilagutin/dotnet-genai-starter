using GenAIPlatform.Application.Core.ModelClients;

namespace GenAIPlatform.Infrastructure.ModelGateway.OpenAi;

internal sealed class OpenAiCompatibleModelClient(OpenAiModelCompletionExecutor executor) : IAiModelClient
{
    public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken) =>
        executor.CompleteAsync(request, cancellationToken);
}
