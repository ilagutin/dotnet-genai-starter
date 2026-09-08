using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Generation.Chat;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Application.Generation.Prompts.Templates;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Domain.Prompts;
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Observability.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed partial class RagChatHandlerTests
{
    private static IApplicationDispatcher CreateHandler(
        CapturingModelClient modelClient,
        CapturingEmbeddingClient embeddingClient,
        CapturingVectorSearchStore vectorSearchStore,
        IUserContext? userContext = null,
        RagOptions? ragOptions = null,
        EmbeddingOptions? embeddingOptions = null,
        ModelGatewayOptions? modelGatewayOptions = null,
        PromptTemplateVersion? promptTemplate = null,
        IAiRequestLogRepository? requestLogRepository = null,
        IPricingRepository? pricingRepository = null)
    {
        var currentUserContext = userContext ?? new FakeUserContext("alice", "tenant-a");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(new Microsoft.Extensions.Configuration.ConfigurationManager());
        services.AddSingleton<IAiModelClient>(modelClient);
        services.AddSingleton<IEmbeddingClient>(embeddingClient);
        services.AddSingleton<IRagVectorSearchStore>(vectorSearchStore);
        services.AddSingleton<IUserContext>(currentUserContext);
        services.AddSingleton<IPromptTemplateProvider>(promptTemplate is null
            ? new InMemoryPromptTemplateProvider()
            : new SingleTemplateProvider(promptTemplate));
        services.AddSingleton<IAiRequestLogRepository>(
            requestLogRepository ?? new CapturingAiRequestLogRepository());
        services.AddSingleton<IPricingRepository>(
            pricingRepository ?? new EmptyPricingRepository());
        services.AddSingleton<ILogger<AiModelRequestLoggingService>>(
            NullLogger<AiModelRequestLoggingService>.Instance);
        services.AddSingleton<ILogger<AiRequestLogWriter>>(
            NullLogger<AiRequestLogWriter>.Instance);
        services.AddSingleton(Options.Create(modelGatewayOptions ?? new ModelGatewayOptions
        {
            DefaultModel = "test-model",
            StrongModel = "test-model-strong",
            CheapModel = "test-model-cheap",
            EvaluationModel = "test-model-evaluation",
            DefaultTemperature = 0.3,
            DefaultMaxOutputTokens = 256,
            MaxOutputTokensLimit = 512
        }));
        services.AddSingleton(Options.Create(embeddingOptions ?? new EmbeddingOptions
        {
            DefaultModel = "test-embedding"
        }));
        services.AddSingleton(Options.Create(ragOptions ?? new RagOptions
        {
            DefaultTopK = 3,
            MaxTopK = 10,
            DefaultMinSimilarityScore = 0.2,
            MaxDocumentFilters = 5,
            MaxContextCharacters = 6000,
            NoContextFallbackMessage = "No matching context."
        }));

        return services
            .BuildServiceProvider()
            .GetRequiredService<IApplicationDispatcher>();
    }

    private static RetrievedDocumentChunk CreateRetrievedChunk(
        string title,
        double similarityScore,
        int position = 0,
        string? text = null,
        string fileName = "notes.md")
    {
        return new RetrievedDocumentChunk(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DocumentVersion: 1,
            position,
            title,
            fileName,
            text ?? $"Content for {title}.",
            similarityScore);
    }

    private static int CountModelInputCharacters(AiModelRequest request)
    {
        return request.Messages.Sum(static message => message.Content.Length);
    }

    private static PromptTemplateVersion CreateRagPromptTemplate(string systemMessage)
    {
        return PromptTemplateVersion.Create(
            RagChatPrompt.TemplateName,
            "test",
            PromptTemplateStatus.Active,
            systemMessage,
            "Question:\n{{question}}\n\nDocument context:\n{{context}}",
            ["question", "context"],
            DateTimeOffset.Parse("2026-05-14T00:00:00Z"),
            "Test RAG prompt.");
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
                Content: "rag answer",
                Model: request.Model,
                Provider: "fake",
                Usage: new AiModelUsage(10, 3, 13),
                CorrelationId: request.CorrelationId));
        }
    }

    private sealed class CapturingEmbeddingClient(
        IReadOnlyList<float>? vector,
        string? model = null,
        string? provider = "fake",
        int? inputTokens = 4)
        : IEmbeddingClient
    {
        public EmbeddingRequest? Request { get; private set; }

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;

            return Task.FromResult(new EmbeddingResponse(
                vector!,
                model ?? request.Model,
                provider!,
                inputTokens,
                request.CorrelationId));
        }
    }

    private sealed class CapturingVectorSearchStore : IRagVectorSearchStore
    {
        public RagVectorSearchQuery? Query { get; private set; }

        public IReadOnlyList<RetrievedDocumentChunk> Results { get; init; } = [];

        public int ReadinessCalls { get; private set; }

        public RagVectorSearchException? ReadinessException { get; init; }

        public Task CheckReadinessAsync(CancellationToken cancellationToken)
        {
            ReadinessCalls++;
            if (ReadinessException is not null)
            {
                throw ReadinessException;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
            RagVectorSearchQuery query,
            CancellationToken cancellationToken)
        {
            Query = query;
            return Task.FromResult(Results);
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

    private sealed class InMemoryPricingRepository(IReadOnlyList<PricingRecord> records) : IPricingRepository
    {
        public Task<PricingRecord?> GetEffectivePricingAsync(
            string provider,
            string model,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            var record = records
                .Where(current =>
                    string.Equals(current.Provider, provider, StringComparison.Ordinal) &&
                    string.Equals(current.Model, model, StringComparison.Ordinal) &&
                    current.EffectiveFromUtc <= usedAtUtc &&
                    (current.EffectiveToUtc is null || current.EffectiveToUtc > usedAtUtc))
                .OrderByDescending(static current => current.EffectiveFromUtc)
                .FirstOrDefault();

            return Task.FromResult(record);
        }
    }

    private sealed class FakeUserContext(
        string? userId,
        string? tenantId,
        bool isAuthenticated = true)
        : IUserContext
    {
        public bool IsAuthenticated => isAuthenticated;

        public string? UserId => userId;

        public string? TenantId => tenantId;

        public IReadOnlyCollection<string> Roles { get; } = ["developer"];

        public IReadOnlyCollection<string> Groups { get; } = ["demo"];
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
