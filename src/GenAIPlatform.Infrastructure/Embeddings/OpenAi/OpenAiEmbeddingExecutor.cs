using System.Text.Json;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Infrastructure.Configuration;

namespace GenAIPlatform.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingExecutor(
    HttpClient httpClient,
    OpenAiEmbeddingOptionsResolver optionsResolver,
    OpenAiEmbeddingRequestFactory requestFactory,
    OpenAiEmbeddingResponseMapper responseMapper,
    OpenAiEmbeddingErrorMapper errorMapper,
    OpenAiEmbeddingRetryPolicy retryPolicy)
{
    public async Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var clientOptions = optionsResolver.Get();
        var endpointUri = optionsResolver.GetEndpointUri(clientOptions);
        var payloadJson = requestFactory.CreatePayloadJson(request);
        var maxRetryAttempts = Math.Max(0, clientOptions.MaxRetryAttempts);

        for (var attempt = 0; ; attempt++)
        {
            using var httpRequest = requestFactory.CreateHttpRequest(
                clientOptions,
                request,
                payloadJson,
                endpointUri);
            try
            {
                var response = await SendAttemptAsync(
                    clientOptions,
                    request,
                    httpRequest,
                    attempt,
                    maxRetryAttempts,
                    cancellationToken);
                if (response is not null)
                {
                    return response;
                }
            }
            catch (TaskCanceledException) when (CanRetryCanceledAttempt(
                                                   cancellationToken,
                                                   attempt,
                                                   maxRetryAttempts))
            {
                await DelayBeforeTransportRetryAsync(
                    clientOptions,
                    attempt,
                    cancellationToken);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw errorMapper.Timeout(exception);
            }
            catch (HttpRequestException) when (attempt < maxRetryAttempts)
            {
                await DelayBeforeTransportRetryAsync(
                    clientOptions,
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

    private static void ValidateRequest(EmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
    }

    private async Task<EmbeddingResponse?> SendAttemptAsync(
        OpenAiCompatibleEmbeddingClientOptions clientOptions,
        EmbeddingRequest request,
        HttpRequestMessage httpRequest,
        int attempt,
        int maxRetryAttempts,
        CancellationToken cancellationToken)
    {
        using var attemptTimeout = CreateAttemptTimeout(
            clientOptions,
            cancellationToken);
        using var httpResponse = await SendHttpRequestAsync(
            httpRequest,
            attemptTimeout.Token);
        var responseContent = await ReadResponseContentAsync(
            httpResponse,
            attemptTimeout.Token);

        if (httpResponse.IsSuccessStatusCode)
        {
            return MapSuccessfulResponse(
                responseContent,
                request);
        }

        if (await TryDelayBeforeRetryableHttpFailureAsync(
                clientOptions,
                httpResponse,
                attempt,
                maxRetryAttempts,
                cancellationToken))
        {
            return null;
        }

        throw errorMapper.FromHttpFailure(
            httpResponse.StatusCode,
            responseContent);
    }

    private static CancellationTokenSource CreateAttemptTimeout(
        OpenAiCompatibleEmbeddingClientOptions clientOptions,
        CancellationToken cancellationToken)
    {
        var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptTimeout.CancelAfter(TimeSpan.FromSeconds(clientOptions.TimeoutSeconds));
        return attemptTimeout;
    }

    private Task<HttpResponseMessage> SendHttpRequestAsync(
        HttpRequestMessage httpRequest,
        CancellationToken cancellationToken) =>
        httpClient.SendAsync(
            httpRequest,
            cancellationToken);

    private static Task<string> ReadResponseContentAsync(
        HttpResponseMessage httpResponse,
        CancellationToken cancellationToken) =>
        httpResponse.Content.ReadAsStringAsync(cancellationToken);

    private EmbeddingResponse MapSuccessfulResponse(
        string responseContent,
        EmbeddingRequest request)
    {
        return responseMapper.Map(
            responseContent,
            request,
            errorMapper);
    }

    private async Task<bool> TryDelayBeforeRetryableHttpFailureAsync(
        OpenAiCompatibleEmbeddingClientOptions clientOptions,
        HttpResponseMessage httpResponse,
        int attempt,
        int maxRetryAttempts,
        CancellationToken cancellationToken)
    {
        if (!retryPolicy.ShouldRetry(httpResponse.StatusCode) || attempt >= maxRetryAttempts)
        {
            return false;
        }

        await retryPolicy.DelayBeforeRetryAsync(
            clientOptions,
            httpResponse,
            attempt,
            cancellationToken);
        return true;
    }

    private static bool CanRetryCanceledAttempt(
        CancellationToken cancellationToken,
        int attempt,
        int maxRetryAttempts) =>
        !cancellationToken.IsCancellationRequested && attempt < maxRetryAttempts;

    private Task DelayBeforeTransportRetryAsync(
        OpenAiCompatibleEmbeddingClientOptions clientOptions,
        int attempt,
        CancellationToken cancellationToken)
    {
        return retryPolicy.DelayBeforeRetryAsync(
            clientOptions,
            response: null,
            attempt,
            cancellationToken);
    }
}
