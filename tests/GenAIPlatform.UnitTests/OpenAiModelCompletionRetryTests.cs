using System.Net;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Infrastructure.Configuration;
using GenAIPlatform.Infrastructure.ModelGateway.OpenAi;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed class OpenAiModelCompletionRetryTests
{
    [Theory]
    [InlineData(408, "provider_timeout")]
    [InlineData(429, "rate_limited")]
    [InlineData(500, "provider_unavailable")]
    [InlineData(502, "provider_unavailable")]
    [InlineData(503, "provider_unavailable")]
    [InlineData(504, "provider_unavailable")]
    [InlineData(501, "provider_unavailable")]
    [InlineData(505, "provider_unavailable")]
    [InlineData(400, "invalid_request")]
    [InlineData(401, "authentication_error")]
    [InlineData(403, "authentication_error")]
    [InlineData(404, "provider_error")]
    public async Task HttpFailure_PreservesTaxonomyAndBoundedSendCount(int status, string error)
    {
        var clock = new ModelRetryTestTimeProvider();
        using var handler = new ModelRetryTestHttpMessageHandler((_, _) =>
            Task.FromResult(ModelRetryTestHttpMessageHandler.Response((HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        var client = CreateClient(http, clock);
        var result = client.CompleteAsync(Request(), TestContext.Current.CancellationToken);
        var retries = status is 408 or 429 or 500 or 502 or 503 or 504 ? 2 : 0;
        for (var attempt = 0; attempt < retries; attempt++)
        {
            await AdvanceBackoffAsync(clock, 100 * (1 << attempt));
        }
        var exception = await Assert.ThrowsAsync<AiModelException>(() => result);
        Assert.Equal(error, exception.ErrorCode);
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Equal(retries + 1, handler.Count);
    }

    [Fact]
    public async Task Retry_PreservesPayloadAndKey_SeparateCallsHaveDistinctKeys()
    {
        var clock = new ModelRetryTestTimeProvider();
        using var handler = new ModelRetryTestHttpMessageHandler((count, _) => Task.FromResult(
            ModelRetryTestHttpMessageHandler.Response(count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        using var http = new HttpClient(handler);
        var client = CreateClient(http, clock);
        var result = client.CompleteAsync(Request(), TestContext.Current.CancellationToken);
        await AdvanceBackoffAsync(clock, 100);
        Assert.Equal("accepted", (await result).Content);
        Assert.Equal(2, handler.Count);
        Assert.False(string.IsNullOrWhiteSpace(handler.Keys[0]));
        Assert.Equal(handler.Keys[0], handler.Keys[1]);
        Assert.Equal(handler.Payloads[0], handler.Payloads[1]);
        await client.CompleteAsync(Request(), TestContext.Current.CancellationToken);
        Assert.NotEqual(handler.Keys[0], handler.Keys[2]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellation_PreventsLaterSend(bool beforeCall)
    {
        var clock = new ModelRetryTestTimeProvider();
        using var handler = new ModelRetryTestHttpMessageHandler((_, _) => Task.FromResult(
            ModelRetryTestHttpMessageHandler.Response(HttpStatusCode.TooManyRequests)));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        if (beforeCall) { cancellation.Cancel(); }
        var result = CreateClient(http, clock).CompleteAsync(Request(), cancellation.Token);
        if (!beforeCall)
        {
            await clock.WaitForDelayAsync(TimeSpan.FromMilliseconds(100));
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(beforeCall ? 0 : 1, handler.Count);
    }

    [Theory]
    [InlineData(false, "transport_error")]
    [InlineData(true, "timeout")]
    public async Task TransportAndTimeout_ExhaustBoundedRetries(bool timeout, string error)
    {
        var clock = new ModelRetryTestTimeProvider();
        using var handler = new ModelRetryTestHttpMessageHandler(async (_, token) =>
        {
            if (timeout)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            throw new HttpRequestException("synthetic transport failure");
        });
        using var http = new HttpClient(handler);
        var result = CreateClient(http, clock).CompleteAsync(Request(), TestContext.Current.CancellationToken);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (timeout)
            {
                await handler.WaitForSendAsync(attempt + 1);
                await clock.WaitForDelayAsync(TimeSpan.FromSeconds(5));
                clock.Advance(TimeSpan.FromSeconds(5));
            }
            if (attempt < 2) { await AdvanceBackoffAsync(clock, 100 * (1 << attempt)); }
        }
        var exception = await Assert.ThrowsAsync<AiModelException>(() => result);
        Assert.Equal(error, exception.ErrorCode);
        Assert.Equal(3, handler.Count);
    }

    private static OpenAiCompatibleModelClient CreateClient(HttpClient http, TimeProvider clock)
    {
        return new(new OpenAiModelCompletionExecutor(http, new OpenAiModelOptionsResolver(Options.Create(new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-key",
            RetryBaseDelayMilliseconds = 100,
            MaxRetryAttempts = 2,
            TimeoutSeconds = 5
        })), new OpenAiModelRequestFactory(), new OpenAiModelResponseMapper(),
            new OpenAiModelErrorMapper(), new OpenAiModelRetryPolicy(clock, () => 0), clock));
    }

    private static AiModelRequest Request() => new("retry-test", "test", [new(AiMessageRole.User, "synthetic prompt")]);

    private static async Task AdvanceBackoffAsync(ModelRetryTestTimeProvider clock, int milliseconds)
    {
        await clock.WaitForDelayAsync(TimeSpan.FromMilliseconds(milliseconds));
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
    }
}
