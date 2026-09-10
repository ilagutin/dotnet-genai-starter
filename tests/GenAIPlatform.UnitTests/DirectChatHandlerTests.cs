using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Generation.Chat;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Application.Generation.Prompts.Templates;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Domain.Prompts;
using GenAIPlatform.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GenAIPlatform.UnitTests;

public sealed class DirectChatHandlerTests
{
    [Fact]
    public async Task HandleAsync_RendersPromptAndCallsModelGateway()
    {
        var modelClient = new CapturingModelClient();
        var dispatcher = CreateDispatcher(modelClient);

        var response = await dispatcher.DispatchAsync<DirectChatCommand, DirectChatResponse>(
            new DirectChatCommand("Explain prompt metadata.", CorrelationId: "test-correlation"),
            CancellationToken.None);

        Assert.Equal("test response", response.Message);
        Assert.Equal("test-model", response.Model);
        Assert.Equal("fake", response.Provider);
        Assert.Equal("test-correlation", response.CorrelationId);
        Assert.Equal(DirectChatPrompt.TemplateName, response.Prompt.TemplateName);
        Assert.Equal("v1", response.Prompt.Version);

        Assert.NotNull(modelClient.Request);
        Assert.Equal("test-correlation", modelClient.Request.CorrelationId);
        Assert.Equal("test-model", modelClient.Request.Model);
        Assert.Equal(0.3, modelClient.Request.Temperature);
        Assert.Equal(256, modelClient.Request.MaxOutputTokens);
        Assert.Equal(DirectChatPrompt.TemplateName, modelClient.Request.Prompt?.TemplateName);
        Assert.Collection(
            modelClient.Request.Messages,
            message => Assert.Equal(AiMessageRole.System, message.Role),
            message =>
            {
                Assert.Equal(AiMessageRole.User, message.Role);
                Assert.Equal("Explain prompt metadata.", message.Content);
            });
    }

    [Fact]
    public async Task HandleAsync_ResolvesConfiguredModelRoute()
    {
        var modelClient = new CapturingModelClient();
        var dispatcher = CreateDispatcher(modelClient);

        await dispatcher.DispatchAsync<DirectChatCommand, DirectChatResponse>(
            new DirectChatCommand("Use the strong route.", Model: "strong"),
            CancellationToken.None);

        Assert.NotNull(modelClient.Request);
        Assert.Equal("test-model-strong", modelClient.Request.Model);
    }

    [Fact]
    public async Task HandleAsync_RejectsUnconfiguredModel()
    {
        var modelClient = new CapturingModelClient();
        var dispatcher = CreateDispatcher(modelClient);

        await Assert.ThrowsAsync<ModelRequestValidationException>(() =>
            dispatcher.DispatchAsync<DirectChatCommand, DirectChatResponse>(
                new DirectChatCommand("Use an arbitrary model.", Model: "unapproved-model"),
                CancellationToken.None));

        Assert.Null(modelClient.Request);
    }

    [Fact]
    public async Task HandleAsync_RejectsOutputTokenLimitByPolicy()
    {
        var dispatcher = CreateDispatcher(new CapturingModelClient());

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            dispatcher.DispatchAsync<DirectChatCommand, DirectChatResponse>(
                new DirectChatCommand("Use too many tokens.", MaxOutputTokens: 4096),
                CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_RejectsInputMessageOverConfiguredLimit()
    {
        var modelClient = new CapturingModelClient();
        var dispatcher = CreateDispatcher(modelClient, maxInputMessageCharacters: 10);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            dispatcher.DispatchAsync<DirectChatCommand, DirectChatResponse>(
                new DirectChatCommand("This message is too long."),
                CancellationToken.None));

        Assert.Null(modelClient.Request);
    }

    [Fact]
    public async Task HandleAsync_RejectsRenderedMessagesOverConfiguredLimit()
    {
        var modelClient = new CapturingModelClient();
        var dispatcher = CreateDispatcher(
            modelClient,
            maxInputMessageCharacters: 20,
            promptTemplate: CreateDirectChatPromptTemplate(new string('s', 15)));

        var exception = await Assert.ThrowsAsync<ModelRequestValidationException>(() =>
            dispatcher.DispatchAsync<DirectChatCommand, DirectChatResponse>(
                new DirectChatCommand("hello!"),
                CancellationToken.None));

        Assert.Equal(
            "Combined model input messages must be 20 characters or fewer.",
            exception.Message);
        Assert.Null(modelClient.Request);
    }

    [Fact]
    public async Task HandleAsync_RejectsInvalidCorrelationId()
    {
        var dispatcher = CreateDispatcher(new CapturingModelClient());

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            dispatcher.DispatchAsync<DirectChatCommand, DirectChatResponse>(
                new DirectChatCommand(
                    "Use an invalid correlation id.",
                    CorrelationId: "invalid\r\nheader"),
                CancellationToken.None));
    }

    private static IApplicationDispatcher CreateDispatcher(
        CapturingModelClient modelClient,
        int maxInputMessageCharacters = 8000,
        PromptTemplateVersion? promptTemplate = null)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{ModelGatewayOptions.SectionName}:DefaultModel"] = "test-model",
            [$"{ModelGatewayOptions.SectionName}:StrongModel"] = "test-model-strong",
            [$"{ModelGatewayOptions.SectionName}:CheapModel"] = "test-model-cheap",
            [$"{ModelGatewayOptions.SectionName}:EvaluationModel"] = "test-model-evaluation",
            [$"{ModelGatewayOptions.SectionName}:DefaultTemperature"] = "0.3",
            [$"{ModelGatewayOptions.SectionName}:DefaultMaxOutputTokens"] = "256",
            [$"{ModelGatewayOptions.SectionName}:MaxInputMessageCharacters"] = maxInputMessageCharacters.ToString(),
            [$"{ModelGatewayOptions.SectionName}:MaxOutputTokensLimit"] = "512"
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddSingleton<IAiModelClient>(modelClient);
        services.AddSingleton<IUserContext>(new FakeUserContext("alice", "tenant-a"));
        services.AddSingleton<IAiRequestLogRepository, CapturingAiRequestLogRepository>();
        services.AddSingleton<IPricingRepository, EmptyPricingRepository>();
        if (promptTemplate is not null)
        {
            services.AddSingleton<IPromptTemplateProvider>(new SingleTemplateProvider(promptTemplate));
        }

        return services
            .BuildServiceProvider()
            .GetRequiredService<IApplicationDispatcher>();
    }

    private static PromptTemplateVersion CreateDirectChatPromptTemplate(string systemMessage)
    {
        return PromptTemplateVersion.Create(
            DirectChatPrompt.TemplateName,
            "test",
            PromptTemplateStatus.Active,
            systemMessage,
            "{{message}}",
            ["message"],
            DateTimeOffset.Parse("2026-05-14T00:00:00Z"),
            "Test direct chat prompt.");
    }

    private sealed class CapturingModelClient : IAiModelClient
    {
        public AiModelRequest? Request { get; private set; }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;

            return Task.FromResult(new AiModelResponse(
                Content: "test response",
                Model: request.Model,
                Provider: "fake",
                Usage: null,
                CorrelationId: request.CorrelationId));
        }
    }

    private sealed class CapturingAiRequestLogRepository : IAiRequestLogRepository
    {
        public List<AiRequestLogEntry> Entries { get; } = [];

        public Task AddAsync(
            AiRequestLogEntry entry,
            CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyPricingRepository : IPricingRepository
    {
        public Task<PricingRecord?> GetEffectivePricingAsync(
            string provider,
            string model,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<PricingRecord?>(null);
        }
    }

    private sealed class FakeUserContext(
        string? userId,
        string? tenantId)
        : IUserContext
    {
        public bool IsAuthenticated => true;

        public string? UserId => userId;

        public string? TenantId => tenantId;

        public IReadOnlyCollection<string> Roles { get; } = [];

        public IReadOnlyCollection<string> Groups { get; } = [];
    }

    private sealed class SingleTemplateProvider(PromptTemplateVersion template) : IPromptTemplateProvider
    {
        public Task<PromptTemplateVersion?> GetActiveVersionAsync(
            string templateName,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<PromptTemplateVersion?>(
                string.Equals(templateName, template.TemplateName, StringComparison.OrdinalIgnoreCase)
                    ? template
                    : null);
        }
    }
}
