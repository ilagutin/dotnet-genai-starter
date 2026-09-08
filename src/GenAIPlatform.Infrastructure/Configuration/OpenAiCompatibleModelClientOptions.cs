namespace GenAIPlatform.Infrastructure.Configuration;

public sealed class OpenAiCompatibleModelClientOptions
{
    public const string SectionName = "GenAIPlatform:ModelGateway:OpenAiCompatible";

    public string BaseUrl { get; init; } = "https://api.openai.com";

    public string ChatCompletionsPath { get; init; } = "/v1/chat/completions";

    public string? ApiKey { get; init; }

    public string? Organization { get; init; }

    public int TimeoutSeconds { get; init; } = 30;

    public int MaxRetryAttempts { get; init; } = 2;

    public int RetryBaseDelayMilliseconds { get; init; } = 200;

    public int RetryMaxDelaySeconds { get; init; } = 30;

    public bool AllowInsecureHttpForLoopback { get; init; }

    public bool IsValid()
    {
        return RetryMaxDelaySeconds is >= 1 and <= 300 && OpenAiCompatibleEndpointPolicy.IsValid(
            ApiKey,
            BaseUrl,
            ChatCompletionsPath,
            AllowInsecureHttpForLoopback,
            TimeoutSeconds,
            MaxRetryAttempts,
            RetryBaseDelayMilliseconds);
    }

    public bool TryCreateEndpointUri(out Uri? endpointUri)
    {
        return OpenAiCompatibleEndpointPolicy.TryCreateEndpointUri(
            BaseUrl,
            ChatCompletionsPath,
            AllowInsecureHttpForLoopback,
            out endpointUri);
    }
}
