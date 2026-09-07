using System.Text.Json;
using GenAIPlatform.Application.Core.ModelClients;

namespace GenAIPlatform.Infrastructure.ModelGateway.OpenAi;

internal sealed class OpenAiModelCompletionExecutor(
    HttpClient httpClient,
    OpenAiModelOptionsResolver optionsResolver,
    OpenAiModelRequestFactory requestFactory,
    OpenAiModelResponseMapper responseMapper,
    OpenAiModelErrorMapper errorMapper,
    OpenAiModelRetryPolicy retryPolicy)
{
    public async Task<AiModelResponse> CompleteAsync(
        AiModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var clientOptions = optionsResolver.Get();
        var endpointUri = optionsResolver.GetEndpointUri(clientOptions);
        var payloadJson = requestFactory.CreatePayloadJson(request);
        var maxRetryAttempts = Math.Max(0, clientOptions.MaxRetryAttempts);
        var idempotencyKey = CreateIdempotencyKey();

        for (var attempt = 0; ; attempt++)
        {
            using var httpRequest = requestFactory.CreateHttpRequest(
                clientOptions,
                request,
                payloadJson,
                endpointUri,
                idempotencyKey);
            try
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(TimeSpan.FromSeconds(clientOptions.TimeoutSeconds));

                using var httpResponse = await httpClient.SendAsync(httpRequest, attemptTimeout.Token);
                var responseContent = await httpResponse.Content.ReadAsStringAsync(attemptTimeout.Token);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    if (retryPolicy.ShouldRetry(httpResponse.StatusCode) && attempt < maxRetryAttempts)
                    {
                        await retryPolicy.DelayBeforeRetryAsync(
                            clientOptions,
                            httpResponse,
                            attempt,
                            cancellationToken);
                        continue;
                    }

                    throw errorMapper.FromHttpFailure(
                        httpResponse.StatusCode,
                        responseContent);
                }

                return responseMapper.Map(
                    responseContent,
                    request,
                    errorMapper);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                attempt < maxRetryAttempts)
            {
                await retryPolicy.DelayBeforeRetryAsync(
                    clientOptions,
                    response: null,
                    attempt,
                    cancellationToken);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw errorMapper.Timeout(exception);
            }
            catch (HttpRequestException) when (attempt < maxRetryAttempts)
            {
                await retryPolicy.DelayBeforeRetryAsync(
                    clientOptions,
                    response: null,
                    attempt,
                    cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw errorMapper.Transport(exception);
            }
            catch (JsonException exception)
            {
                throw errorMapper.InvalidJson(exception);
            }
        }
    }

    private static string CreateIdempotencyKey()
    {
        return $"genai-{Guid.NewGuid():N}";
    }
}
