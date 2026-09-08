using System.Net;
using GenAIPlatform.Application.Core.Configuration;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Evaluations.StartRun;
using GenAIPlatform.Application.Generation.Chat;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Evaluations;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Observability.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed partial class EvaluationRunHandlerTests
{
    private static IApplicationDispatcher CreateHandler(
        IEvaluationDatasetProvider? datasetProvider = null,
        IAiModelClient? modelClient = null,
        IEmbeddingClient? embeddingClient = null,
        IRagVectorSearchStore? vectorSearchStore = null,
        CapturingAiRequestLogRepository? logRepository = null,
        CapturingEvaluationRunRepository? runRepository = null,
        RagOptions? ragOptions = null,
        ApplicationOptions? applicationOptions = null)
    {
        var currentLogRepository = logRepository ?? new CapturingAiRequestLogRepository();
        var userContext = new FakeUserContext();
        var pricingRepository = new FixedPricingRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(new Microsoft.Extensions.Configuration.ConfigurationManager());
        services.AddSingleton<IEvaluationDatasetProvider>(
            datasetProvider ?? new InMemoryEvaluationDatasetProvider());
        services.AddSingleton<IEvaluationRunRepository>(
            runRepository ?? new CapturingEvaluationRunRepository());
        services.AddSingleton<IAiModelClient>(
            modelClient ?? new EchoModelClient());
        services.AddSingleton<IEmbeddingClient>(
            embeddingClient ?? new CapturingEmbeddingClient());
        services.AddSingleton<IRagVectorSearchStore>(
            vectorSearchStore ?? new CapturingVectorSearchStore());
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton<IAiRequestLogRepository>(currentLogRepository);
        services.AddSingleton<IPricingRepository>(pricingRepository);
        services.AddSingleton<IPromptTemplateProvider>(new InMemoryPromptTemplateProvider());
        services.AddSingleton(Options.Create(applicationOptions ?? new ApplicationOptions()));
        services.AddSingleton<ILogger<AiModelRequestLoggingService>>(
            NullLogger<AiModelRequestLoggingService>.Instance);
        services.AddSingleton<ILogger<AiRequestLogWriter>>(
            NullLogger<AiRequestLogWriter>.Instance);
        services.AddSingleton<ILogger<EvaluationRunCompletionCoordinator>>(
            NullLogger<EvaluationRunCompletionCoordinator>.Instance);
        services.AddSingleton(Options.Create(new ModelGatewayOptions
        {
            DefaultModel = "mock-chat",
            StrongModel = "mock-chat-strong",
            CheapModel = "mock-chat-cheap",
            EvaluationModel = "mock-chat-evaluation",
            DefaultTemperature = 0.2,
            DefaultMaxOutputTokens = 256,
            MaxOutputTokensLimit = 512
        }));
        services.AddSingleton(Options.Create(new EmbeddingOptions { Provider = "mock", DefaultModel = "mock-embedding" }));
        services.AddSingleton(Options.Create(ragOptions ?? new RagOptions
        {
            DefaultTopK = 5,
            MaxTopK = 20,
            DefaultMinSimilarityScore = 0.2,
            MaxDocumentFilters = 50,
            MaxContextCharacters = 6000,
            NoContextFallbackMessage = "No context."
        }));

        return services
            .BuildServiceProvider()
            .GetRequiredService<IApplicationDispatcher>();
    }

    private static readonly RetrievedDocumentChunk[] SampleEvaluationChunks =
    [
        new RetrievedDocumentChunk(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            1,
            0,
            "Evaluation sample context",
            "evaluation.md",
            "The starter kit uses Clean Architecture. AI request logging records model calls. Access filters are applied before prompting. Cost tracking reports estimated cost. Evidence-backed answers include a citation marker such as [1].",
            0.99)
    ];

    private static EvaluationDataset CreateRetrievalBackedDataset(string version)
    {
        return new EvaluationDataset(
            version,
            [
                new EvaluationCase(
                    "case-1",
                    "Retrieval-backed case",
                    "Answer from retrieved context.",
                    [new EvaluationCheck("required_phrase", Phrase: "Clean Architecture")],
                    Context: null)
            ]);
    }

    private sealed class CapturingModelClient : IAiModelClient
    {
        public List<AiModelRequest> Requests { get; } = [];

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var userMessage = request.Messages.Last(static message => message.Role == AiMessageRole.User).Content;
            return Task.FromResult(new AiModelResponse(
                userMessage,
                request.Model,
                "mock",
                new AiModelUsage(20, 10, 30),
                request.CorrelationId));
        }
    }

    private sealed class EchoModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            var userMessage = request.Messages.Last(static message => message.Role == AiMessageRole.User).Content;
            return Task.FromResult(new AiModelResponse(
                userMessage,
                request.Model,
                "mock",
                new AiModelUsage(20, 10, 30),
                request.CorrelationId));
        }
    }

    private sealed class CancelAfterResponseModelClient(CancellationTokenSource cancellation) : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            var userMessage = request.Messages.Last(static message => message.Role == AiMessageRole.User).Content;
            var response = new AiModelResponse(
                userMessage,
                request.Model,
                "mock",
                new AiModelUsage(20, 10, 30),
                request.CorrelationId);
            cancellation.Cancel();
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            throw new AiModelException(
                "mock",
                "provider unavailable",
                "provider_unavailable",
                HttpStatusCode.BadGateway);
        }
    }

    private sealed class CapturingEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new EmbeddingResponse(
                [1f, 0f],
                request.Model,
                "mock",
                4,
                request.CorrelationId));
        }
    }

    private sealed class ThrowingEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Embedding should not run.");
        }
    }

    private sealed class CapturingVectorSearchStore : IRagVectorSearchStore
    {
        public Action? OnCheckReadiness { get; init; }

        public Action? OnSearch { get; init; }

        public RagVectorSearchException? SearchException { get; init; }

        public IReadOnlyList<RetrievedDocumentChunk> Chunks { get; init; } = SampleEvaluationChunks;

        public Task CheckReadinessAsync(CancellationToken cancellationToken)
        {
            OnCheckReadiness?.Invoke();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
            RagVectorSearchQuery query,
            CancellationToken cancellationToken)
        {
            OnSearch?.Invoke();
            if (SearchException is not null)
            {
                throw SearchException;
            }

            return Task.FromResult(Chunks);
        }
    }

    private sealed class FixedEvaluationDatasetProvider(EvaluationDataset dataset) : IEvaluationDatasetProvider
    {
        public Task<EvaluationDataset> GetDatasetAsync(
            string? datasetVersion,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(dataset);
        }
    }

    private sealed class CapturingAiRequestLogRepository : IAiRequestLogRepository
    {
        public List<AiRequestLogEntry> Entries { get; } = [];

        public Task AddAsync(AiRequestLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingEvaluationRunRepository : IEvaluationRunRepository
    {
        private readonly Dictionary<Guid, CapturedEvaluationRun> runs = [];

        public string? CompletedStatus { get; private set; }

        public bool ThrowOnAddCaseResult { get; init; }

        public Exception? AddCaseResultException { get; init; }

        public OperationCanceledException? OperationCanceledOnAddCaseResult { get; init; }

        public int ThrowOnCompleteRunAttempts { get; init; }

        public IReadOnlyList<Exception> CompleteRunExceptions { get; init; } = [];

        private int completeRunAttemptsThrown;

        public List<EvaluationCaseResult> CaseResults { get; } = [];

        public List<string> CompletionAttemptStatuses { get; } = [];

        public Task AddRunAsync(
            EvaluationRunResult run,
            string tenantId,
            string userId,
            CancellationToken cancellationToken)
        {
            runs[run.RunId] = new CapturedEvaluationRun(run, tenantId, userId);
            return Task.CompletedTask;
        }

        public Task AddCaseResultAsync(Guid runId, EvaluationCaseResult result, CancellationToken cancellationToken)
        {
            if (OperationCanceledOnAddCaseResult is not null)
            {
                throw OperationCanceledOnAddCaseResult;
            }

            if (AddCaseResultException is not null)
            {
                throw AddCaseResultException;
            }

            if (ThrowOnAddCaseResult)
            {
                throw new InvalidOperationException("case persistence failed");
            }

            CaseResults.Add(result);
            if (runs.TryGetValue(runId, out var captured))
            {
                runs[runId] = captured with
                {
                    Run = captured.Run with { Cases = captured.Run.Cases.Concat([result]).ToArray() }
                };
            }

            return Task.CompletedTask;
        }

        public Task CompleteRunAsync(Guid runId, string status, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
        {
            CompletionAttemptStatuses.Add(status);
            if (completeRunAttemptsThrown < CompleteRunExceptions.Count)
            {
                throw CompleteRunExceptions[completeRunAttemptsThrown++];
            }

            if (completeRunAttemptsThrown < ThrowOnCompleteRunAttempts)
            {
                completeRunAttemptsThrown++;
                throw new InvalidOperationException("run completion failed");
            }

            CompletedStatus = status;
            if (runs.TryGetValue(runId, out var captured))
            {
                runs[runId] = captured with
                {
                    Run = captured.Run with { Status = status, CompletedAtUtc = completedAtUtc }
                };
            }

            return Task.CompletedTask;
        }

        public Task<EvaluationRunResult?> GetRunAsync(
            Guid runId,
            string tenantId,
            string userId,
            CancellationToken cancellationToken)
        {
            if (!runs.TryGetValue(runId, out var captured) ||
                !string.Equals(captured.TenantId, tenantId, StringComparison.Ordinal) ||
                !string.Equals(captured.UserId, userId, StringComparison.Ordinal))
            {
                return Task.FromResult<EvaluationRunResult?>(null);
            }

            return Task.FromResult<EvaluationRunResult?>(captured.Run);
        }

        public Task<EvaluationRunSummary?> GetSummaryAsync(
            Guid runId,
            string tenantId,
            string userId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<EvaluationRunSummary?>(null);
        }

        private sealed record CapturedEvaluationRun(
            EvaluationRunResult Run,
            string TenantId,
            string UserId);
    }

    private sealed class FixedPricingRepository : IPricingRepository
    {
        public Task<PricingRecord?> GetEffectivePricingAsync(
            string provider,
            string model,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<PricingRecord?>(new PricingRecord(
                Guid.NewGuid(),
                provider,
                model,
                "USD",
                0,
                0,
                0,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                EffectiveToUtc: null));
        }
    }

    private sealed class FakeUserContext : IUserContext
    {
        public bool IsAuthenticated { get; init; } = true;

        public string? UserId { get; init; } = "alice";

        public string? TenantId { get; init; } = "tenant-a";

        public IReadOnlyCollection<string> Roles { get; } = ["developer"];

        public IReadOnlyCollection<string> Groups { get; } = ["demo"];
    }
}
