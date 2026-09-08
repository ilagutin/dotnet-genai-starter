using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Infrastructure;
using GenAIPlatform.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.IntegrationTests;

public sealed class OpenAiCompatibleClientOptionsTests
{
    [Theory]
    [InlineData(null, 30)]
    [InlineData("1", 1)]
    [InlineData("300", 300)]
    public void ModelRetryCap_BindsDefaultAndBoundaries(string? value, int expected)
    {
        using var provider = CreateProvider(value, "OpenAiCompatible");
        var options = provider.GetRequiredService<IOptions<OpenAiCompatibleModelClientOptions>>().Value;
        Assert.Equal(expected, options.RetryMaxDelaySeconds);
        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("301")]
    public async Task ModelRetryCap_InvalidActiveConfigurationFailsBeforeSend(string value)
    {
        using var provider = CreateProvider(value, "OpenAiCompatible");
        var client = provider.GetRequiredService<IAiModelClient>();
        var exception = await Assert.ThrowsAsync<AiModelException>(() => client.CompleteAsync(
            new("config-test", "test", [new(AiMessageRole.User, "test")]), TestContext.Current.CancellationToken));
        Assert.Equal("configuration_error", exception.ErrorCode);
    }

    [Fact]
    public async Task MockProvider_DefaultConfigurationStillCompletes()
    {
        using var provider = CreateProvider(null, "Mock");
        var client = provider.GetRequiredService<IAiModelClient>();
        var response = await client.CompleteAsync(new("mock-test", "test", [new(AiMessageRole.User, "test")]),
            TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(response.Content));
    }

    private static ServiceProvider CreateProvider(string? cap, string selectedProvider)
    {
        var settings = new Dictionary<string, string?>
        {
            ["GenAIPlatform:ModelGateway:Provider"] = selectedProvider,
            ["GenAIPlatform:ModelGateway:OpenAiCompatible:ApiKey"] = "test-key"
        };
        if (cap is not null)
        {
            settings["GenAIPlatform:ModelGateway:OpenAiCompatible:RetryMaxDelaySeconds"] = cap;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("/v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
    [InlineData("v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
    public void ModelClientOptions_AcceptsRelativeEndpointPath(
        string endpointPath,
        string expectedEndpoint)
    {
        var options = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            ChatCompletionsPath = endpointPath
        };

        var created = options.TryCreateEndpointUri(out var endpointUri);

        Assert.True(created);
        Assert.True(options.IsValid());
        Assert.Equal(expectedEndpoint, endpointUri!.ToString().TrimEnd('/'));
    }

    [Theory]
    [InlineData("/v1/embeddings", "https://api.openai.com/v1/embeddings")]
    [InlineData("v1/embeddings", "https://api.openai.com/v1/embeddings")]
    public void EmbeddingClientOptions_AcceptsRelativeEndpointPath(
        string endpointPath,
        string expectedEndpoint)
    {
        var options = new OpenAiCompatibleEmbeddingClientOptions
        {
            ApiKey = "test-api-key",
            EmbeddingsPath = endpointPath
        };

        var created = options.TryCreateEndpointUri(out var endpointUri);

        Assert.True(created);
        Assert.True(options.IsValid());
        Assert.Equal(expectedEndpoint, endpointUri!.ToString().TrimEnd('/'));
    }

    [Theory]
    [InlineData("https://provider.example/v1/chat/completions")]
    [InlineData("//provider.example/v1/chat/completions")]
    public void ModelClientOptions_RejectsAbsoluteEndpointPath(string endpointPath)
    {
        var options = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            ChatCompletionsPath = endpointPath
        };

        Assert.False(options.TryCreateEndpointUri(out _));
        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData("https://provider.example/v1/embeddings")]
    [InlineData("//provider.example/v1/embeddings")]
    public void EmbeddingClientOptions_RejectsAbsoluteEndpointPath(string endpointPath)
    {
        var options = new OpenAiCompatibleEmbeddingClientOptions
        {
            ApiKey = "test-api-key",
            EmbeddingsPath = endpointPath
        };

        Assert.False(options.TryCreateEndpointUri(out _));
        Assert.False(options.IsValid());
    }
}
