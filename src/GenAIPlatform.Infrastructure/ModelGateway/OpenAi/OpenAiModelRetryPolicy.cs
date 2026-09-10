using System.Net;
using System.Net.Http.Headers;
using GenAIPlatform.Infrastructure.Configuration;

namespace GenAIPlatform.Infrastructure.ModelGateway.OpenAi;

internal sealed class OpenAiModelRetryPolicy(TimeProvider? timeProvider = null, Func<double>? nextRandom = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Func<double> random = nextRandom ?? Random.Shared.NextDouble;

    public bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int)statusCode is >= 500 and <= 599 and not 501 and not 505;
    }

    public Task DelayBeforeRetryAsync(
        OpenAiCompatibleModelClientOptions clientOptions,
        HttpResponseMessage? response,
        int attempt,
        CancellationToken cancellationToken)
    {
        var capMilliseconds = Math.Clamp(clientOptions.RetryMaxDelaySeconds, 1, 300) * 1000d;
        var baseMilliseconds = Math.Max(1, clientOptions.RetryBaseDelayMilliseconds);
        var fallbackMilliseconds = baseMilliseconds * Math.Pow(2, Math.Clamp(attempt, 0, 31));
        var delayMilliseconds = Math.Min(capMilliseconds, ReadHintMilliseconds(response) ?? fallbackMilliseconds);
        var jitter = Math.Clamp(random(), 0, 1);
        delayMilliseconds = Math.Min(capMilliseconds, delayMilliseconds * (1 + 0.2 * jitter));
        return Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), clock, cancellationToken);
    }

    private double? ReadHintMilliseconds(HttpResponseMessage? response)
    {
        if (response is null || !response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        // TryParse rejects malformed or overflowing values without exposing the raw header.
        if (!RetryConditionHeaderValue.TryParse(string.Join(",", values), out var hint))
        {
            return null;
        }

        var delay = hint.Delta ?? (hint.Date - clock.GetUtcNow());
        return delay is { } positive && positive > TimeSpan.Zero ? positive.TotalMilliseconds : null;
    }
}
