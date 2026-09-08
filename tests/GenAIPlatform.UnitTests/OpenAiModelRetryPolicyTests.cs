using System.Net;
using GenAIPlatform.Infrastructure.ModelGateway.OpenAi;

namespace GenAIPlatform.UnitTests;

public sealed class OpenAiModelRetryPolicyTests
{
    [Theory]
    [InlineData("60", 30, 0, 30000)]
    [InlineData("60", 30, 1, 30000)]
    [InlineData("10", 30, 0, 10000)]
    [InlineData("10", 30, 1, 12000)]
    [InlineData("60", 120, 0.5, 66000)]
    [InlineData("60", 1, 1, 1000)]
    [InlineData("Thu, 01 Jan 2026 00:00:10 GMT", 30, 0, 10000)]
    [InlineData("Thu, 01 Jan 2026 00:01:00 GMT", 30, 1, 30000)]
    [InlineData("2147483647", 300, 1, 300000)]
    public async Task Hint_IsJitteredThenClamped(string hint, int cap, double random, int milliseconds)
    {
        await AssertDelayAsync(hint, cap, 200, 0, random, milliseconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nonsense")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99999999999999999999999999999999999999999999999")]
    [InlineData("Wed, 31 Dec 2025 23:59:59 GMT")]
    [InlineData("Thu, 01 Jan 2026 00:00:00 GMT")]
    public async Task UnusableHint_UsesExponentialFallback(string? hint)
    {
        await AssertDelayAsync(hint, 30, 200, 2, 0, 800);
    }

    [Theory]
    [InlineData(0, 200, 0, 200)]
    [InlineData(0, 200, 1, 240)]
    [InlineData(3, 200, 1, 1920)]
    [InlineData(int.MaxValue, int.MaxValue, 1, 30000)]
    [InlineData(30, 1, 0, 30000)]
    public async Task Fallback_BoundsArithmeticAndJitter(int attempt, int baseDelay, double random, int milliseconds)
    {
        await AssertDelayAsync(null, 30, baseDelay, attempt, random, milliseconds);
    }

    [Fact]
    public void StatusPolicy_ExcludesUnsupportedStatuses()
    {
        var policy = new OpenAiModelRetryPolicy();
        for (var status = 100; status <= 699; status++)
        {
            Assert.Equal(status is 408 or 429 || status is >= 500 and <= 599 and not 501 and not 505,
                policy.ShouldRetry((HttpStatusCode)status));
        }
    }

    private static async Task AssertDelayAsync(string? hint, int cap, int baseDelay, int attempt, double random, int milliseconds)
    {
        var clock = new ModelRetryTestTimeProvider();
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (hint is not null) { response.Headers.TryAddWithoutValidation("Retry-After", hint); }
        var policy = new OpenAiModelRetryPolicy(clock, () => random);
        var delay = policy.DelayBeforeRetryAsync(new()
        {
            RetryMaxDelaySeconds = cap,
            RetryBaseDelayMilliseconds = baseDelay
        }, response, attempt, TestContext.Current.CancellationToken);
        Assert.False(delay.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds - 1));
        Assert.False(delay.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await delay.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(delay.IsCompletedSuccessfully);
    }
}
