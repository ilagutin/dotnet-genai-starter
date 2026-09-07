using System.Net;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GenAIPlatform.IntegrationTests;

public sealed class OpenAiCompatibleEmbeddingClientTests
{
    [Fact]
    public async Task CreateEmbeddingAsync_RetriesTransientProviderStatus()
    {
        await using var app = CreateFakeOpenAiCompatibleServer();
        await app.StartAsync();
        var baseUrl = GetServerAddress(app);
        using var provider = CreateEmbeddingServiceProvider(
            baseUrl,
            new Dictionary<string, string?>
            {
                ["GenAIPlatform:Embeddings:OpenAiCompatible:MaxRetryAttempts"] = "1"
            });
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var response = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest("hello", "embedding-model", "embedding-retry-test"),
            TestContext.Current.CancellationToken);

        Assert.Equal("embedding-model", response.Model);
        Assert.Equal("openai-compatible", response.Provider);
        Assert.Equal([0.1f, 0.2f, 0.3f], response.Vector);
        Assert.Equal(1, response.InputTokens);
        Assert.Equal(2, app.Services.GetRequiredService<AttemptCounter>().Value);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.BadRequest, "invalid_request")]
    [InlineData((int)HttpStatusCode.Unauthorized, "authentication_error")]
    [InlineData((int)HttpStatusCode.Forbidden, "authentication_error")]
    [InlineData((int)HttpStatusCode.RequestTimeout, "provider_timeout")]
    [InlineData((int)HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData((int)HttpStatusCode.InternalServerError, "provider_unavailable")]
    public async Task CreateEmbeddingAsync_NormalizesProviderErrorStatus(
        int statusCodeValue,
        string expectedErrorCode)
    {
        await using var app = CreateFakeOpenAiCompatibleServer(async context =>
        {
            context.Response.StatusCode = statusCodeValue;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = "provider failed",
                    code = "raw_provider_code"
                }
            });
        });
        await app.StartAsync();
        using var provider = CreateEmbeddingServiceProvider(GetServerAddress(app));
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-error-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("openai-compatible", exception.Provider);
        Assert.Equal(expectedErrorCode, exception.ErrorCode);
        Assert.Equal((HttpStatusCode)statusCodeValue, exception.StatusCode);
        Assert.Equal("raw_provider_code", exception.ProviderErrorCode);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_NormalizesInvalidProviderConfiguration()
    {
        using var provider = CreateEmbeddingServiceProvider("not-a-valid-uri");
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-config-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("configuration_error", exception.ErrorCode);
        Assert.Null(exception.StatusCode);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_RejectsHttpProviderEndpointByDefault()
    {
        using var provider = CreateEmbeddingServiceProvider(
            "http://127.0.0.1:12345",
            new Dictionary<string, string?>
            {
                ["GenAIPlatform:Embeddings:OpenAiCompatible:AllowInsecureHttpForLoopback"] = "false"
            });
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-http-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("configuration_error", exception.ErrorCode);
        Assert.Null(exception.StatusCode);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_NormalizesInvalidJsonResponse()
    {
        await using var app = CreateFakeOpenAiCompatibleServer(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("not-json");
        });
        await app.StartAsync();
        using var provider = CreateEmbeddingServiceProvider(GetServerAddress(app));
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-json-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("invalid_json", exception.ErrorCode);
        Assert.Null(exception.StatusCode);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_RejectsEmptyEmbeddingResponse()
    {
        await using var app = CreateFakeOpenAiCompatibleServer(async context =>
        {
            await context.Response.WriteAsJsonAsync(new
            {
                model = "embedding-model",
                data = new[]
                {
                    new
                    {
                        embedding = Array.Empty<float>()
                    }
                },
                usage = new
                {
                    prompt_tokens = 1,
                    total_tokens = 1
                }
            });
        });
        await app.StartAsync();
        using var provider = CreateEmbeddingServiceProvider(GetServerAddress(app));
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-empty-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("empty_embedding", exception.ErrorCode);
        Assert.Null(exception.StatusCode);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_NormalizesProviderTimeout()
    {
        await using var app = CreateFakeOpenAiCompatibleServer(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), context.RequestAborted);
        });
        await app.StartAsync();
        using var provider = CreateEmbeddingServiceProvider(
            GetServerAddress(app),
            new Dictionary<string, string?>
            {
                ["GenAIPlatform:Embeddings:OpenAiCompatible:TimeoutSeconds"] = "1"
            });
        var embeddingClient = provider.GetRequiredService<IEmbeddingClient>();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest("hello", "embedding-model", "embedding-timeout-test"),
                TestContext.Current.CancellationToken));

        Assert.Equal("timeout", exception.ErrorCode);
        Assert.Null(exception.StatusCode);
    }

    private static WebApplication CreateFakeOpenAiCompatibleServer()
    {
        return CreateFakeOpenAiCompatibleServer(async context =>
        {
            var attempts = context.RequestServices.GetRequiredService<AttemptCounter>();
            attempts.Value++;

            if (attempts.Value == 1)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        message = "rate limit",
                        code = "rate_limit"
                    }
                });
                return;
            }

            await context.Response.WriteAsJsonAsync(new
            {
                model = "embedding-model",
                data = new[]
                {
                    new
                    {
                        embedding = new[] { 0.1f, 0.2f, 0.3f }
                    }
                },
                usage = new
                {
                    prompt_tokens = 1,
                    total_tokens = 1
                }
            });
        });
    }

    private static WebApplication CreateFakeOpenAiCompatibleServer(Func<HttpContext, Task> handler)
    {
        var builder = LoopbackTestServer.CreateBuilder();
        builder.Services.AddSingleton<AttemptCounter>();

        var app = builder.Build();
        app.MapPost("/v1/embeddings", handler);
        return app;
    }

    private static ServiceProvider CreateEmbeddingServiceProvider(
        string baseUrl,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var configurationValues = new Dictionary<string, string?>
        {
            ["GenAIPlatform:Embeddings:Provider"] = "OpenAiCompatible",
            ["GenAIPlatform:Embeddings:DefaultModel"] = "embedding-model",
            ["GenAIPlatform:Embeddings:OpenAiCompatible:BaseUrl"] = baseUrl,
            ["GenAIPlatform:Embeddings:OpenAiCompatible:ApiKey"] = "test-api-key",
            ["GenAIPlatform:Embeddings:OpenAiCompatible:AllowInsecureHttpForLoopback"] = "true",
            ["GenAIPlatform:Embeddings:OpenAiCompatible:MaxRetryAttempts"] = "0",
            ["GenAIPlatform:Embeddings:OpenAiCompatible:RetryBaseDelayMilliseconds"] = "1",
            ["GenAIPlatform:Embeddings:OpenAiCompatible:TimeoutSeconds"] = "30"
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                configurationValues[key] = value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static string GetServerAddress(WebApplication app)
    {
        return LoopbackTestServer.GetAddress(app);
    }

    private sealed class AttemptCounter
    {
        public int Value { get; set; }
    }
}
