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
using GenAIPlatform.Domain.Exceptions;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Observability.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed class EvaluationRunHandlerTests
{
    [Fact]
    public async Task GetEvaluationRunAsync_ReturnsNullForSameTenantDifferentUser()
    {
        var runId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var runRepository = new CapturingEvaluationRunRepository();
        await runRepository.AddRunAsync(
            new EvaluationRunResult(
                runId,
                "sample-v1",
                "runner-v1",
                "v1",
                "mock-chat-evaluation",
                "{}",
                "{}",
                "Succeeded",
                DateTimeOffset.Parse("2026-05-15T12:00:00Z"),
                DateTimeOffset.Parse("2026-05-15T12:01:00Z"),
                [
                    new EvaluationCaseResult(
                        "case-1",
                        "Case 1",
                        "Passed",
                        "answer derived from alice-only chunks",
                        RetrievedCount: 1,
                        RetrievalHit: true,
                        TimeSpan.FromMilliseconds(10),
                        EstimatedCost: 0.001m,
                        CostCurrency: "USD",
                        ErrorCode: null,
                        ErrorMessage: null,
                        [])
                ]),
            "tenant-a",
            "alice",
            CancellationToken.None);
        var handler = new GetEvaluationRunHandler(
            runRepository,
            new FakeUserContext { UserId = "bob", TenantId = "tenant-a" });

        var result = await handler.HandleAsync(new GetEvaluationRunQuery(runId), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task HandleAsync_LogsEvaluationModelCallsThroughLoggingService()
    {
        var logRepository = new CapturingAiRequestLogRepository();
        var runRepository = new CapturingEvaluationRunRepository();
        var handler = CreateHandler(logRepository: logRepository, runRepository: runRepository);

        var result = await handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(CorrelationId: "eval-log-test"),
            CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        Assert.NotEmpty(logRepository.Entries);
        Assert.All(logRepository.Entries, entry =>
        {
            Assert.StartsWith("eval-log-test-", entry.CorrelationId, StringComparison.Ordinal);
            Assert.Equal("evaluation-answer", entry.Prompt?.TemplateName);
        });
    }

    [Fact]
    public async Task HandleAsync_UsesConfiguredEvaluationRunnerVersion()
    {
        var handler = CreateHandler(
            applicationOptions: new ApplicationOptions
            {
                ApiVersion = "v1",
                RunnerVersion = "runner-test"
            });

        var result = await handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(CorrelationId: "eval-runner-version-test"),
            CancellationToken.None);

        Assert.Equal("runner-test", result.RunnerVersion);
    }

    [Fact]
    public async Task HandleAsync_CapsEvaluationContextAtConfiguredRagLimit()
    {
        var maxContextCharacters = 500;
        var modelClient = new CapturingModelClient();
        var handler = CreateHandler(
            datasetProvider: new FixedEvaluationDatasetProvider(
                new EvaluationDataset(
                    "retrieval-context-test-v1",
                    [
                        new EvaluationCase(
                            "case-1",
                            "Retrieval context cap test",
                            "Answer from retrieved context.",
                            [new EvaluationCheck("required_phrase", Phrase: "A")])
                    ])),
            modelClient: modelClient,
            vectorSearchStore: new CapturingVectorSearchStore
            {
                Chunks =
                [
                    new RetrievedDocumentChunk(
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        1,
                        0,
                        "Oversized context",
                        "oversized.md",
                        new string('A', 900),
                        0.99)
                ]
            },
            ragOptions: new RagOptions
            {
                DefaultTopK = 5,
                MaxTopK = 20,
                DefaultMinSimilarityScore = 0.2,
                MaxDocumentFilters = 50,
                MaxContextCharacters = maxContextCharacters,
                NoContextFallbackMessage = "No context."
            });

        await handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(DatasetVersion: "retrieval-context-test-v1", CorrelationId: "eval-context-limit"),
            CancellationToken.None);

        Assert.NotEmpty(modelClient.Requests);
        Assert.All(modelClient.Requests, request =>
        {
            var userMessage = request.Messages.Last(static message => message.Role == AiMessageRole.User).Content;
            var contextStart = userMessage.IndexOf("Document context:\n", StringComparison.Ordinal);
            Assert.True(contextStart >= 0);
            contextStart += "Document context:\n".Length;
            var context = userMessage[contextStart..];
            Assert.True(context.Length <= maxContextCharacters);
        });
    }

    [Fact]
    public async Task HandleAsync_LogsOnlyEvaluationChunksIncludedInPromptContext()
    {
        const string includedContext = "[1] Included\nincluded evidence";
        var includedDocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var excludedDocumentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var logRepository = new CapturingAiRequestLogRepository();
        var modelClient = new CapturingModelClient();
        var handler = CreateHandler(
            datasetProvider: new FixedEvaluationDatasetProvider(
                new EvaluationDataset(
                    "retrieval-log-context-test-v1",
                    [
                        new EvaluationCase(
                            "case-1",
                            "Retrieval context log test",
                            "Answer from retrieved context.",
                            [new EvaluationCheck("required_phrase", Phrase: "included evidence")])
                    ])),
            modelClient: modelClient,
            logRepository: logRepository,
            vectorSearchStore: new CapturingVectorSearchStore
            {
                Chunks =
                [
                    new RetrievedDocumentChunk(
                        includedDocumentId,
                        Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        1,
                        0,
                        "Included",
                        "included.md",
                        "included evidence",
                        0.99),
                    new RetrievedDocumentChunk(
                        excludedDocumentId,
                        Guid.Parse("44444444-4444-4444-4444-444444444444"),
                        1,
                        1,
                        "Excluded",
                        "excluded.md",
                        "excluded evidence",
                        0.98)
                ]
            },
            ragOptions: new RagOptions
            {
                DefaultTopK = 5,
                MaxTopK = 20,
                DefaultMinSimilarityScore = 0.2,
                MaxDocumentFilters = 50,
                MaxContextCharacters = includedContext.Length,
                NoContextFallbackMessage = "No context."
            });

        var result = await handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(DatasetVersion: "retrieval-log-context-test-v1", CorrelationId: "eval-context-log"),
            CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        var userMessage = Assert
            .Single(modelClient.Requests)
            .Messages
            .Last(static message => message.Role == AiMessageRole.User)
            .Content;
        Assert.Contains("included evidence", userMessage);
        Assert.DoesNotContain("excluded evidence", userMessage);
        var retrievedDocument = Assert.Single(Assert.Single(logRepository.Entries).RetrievedDocuments);
        Assert.Equal(includedDocumentId, retrievedDocument.DocumentId);
    }

    [Fact]
    public async Task HandleAsync_UsesFixtureContextWhenProvided()
    {
        var logRepository = new CapturingAiRequestLogRepository();
        var modelClient = new CapturingModelClient();
        var handler = CreateHandler(
            datasetProvider: new FixedEvaluationDatasetProvider(
                new EvaluationDataset(
                    "fixture-context-test-v1",
                    [
                        new EvaluationCase(
                            "case-1",
                            "Fixture context test",
                            "Answer from fixture context.",
                            [new EvaluationCheck("required_phrase", Phrase: "fixture-only phrase")],
                            Context: "[1] fixture-only phrase")
                    ])),
            embeddingClient: new ThrowingEmbeddingClient(),
            modelClient: modelClient,
            logRepository: logRepository,
            vectorSearchStore: new CapturingVectorSearchStore
            {
                OnCheckReadiness = () => throw new InvalidOperationException("Readiness should not run."),
                OnSearch = () => throw new InvalidOperationException("Search should not run."),
                Chunks =
                [
                    new RetrievedDocumentChunk(
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        1,
                        0,
                        "Unrelated retrieval",
                        "unrelated.md",
                        "retrieval-only phrase",
                        0.99)
                ]
            });

        var result = await handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(DatasetVersion: "fixture-context-test-v1", CorrelationId: "eval-fixture-context"),
            CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        var request = Assert.Single(modelClient.Requests);
        var userMessage = request.Messages.Last(static message => message.Role == AiMessageRole.User).Content;
        Assert.Contains("fixture-only phrase", userMessage);
        Assert.DoesNotContain("retrieval-only phrase", userMessage);
        var logEntry = Assert.Single(logRepository.Entries);
        Assert.Null(logEntry.EmbeddingTokens);
        Assert.Empty(logEntry.RetrievedDocuments);
        Assert.Equal(TimeSpan.Zero, logEntry.RetrievalLatency);
    }

    [Fact]
    public async Task HandleAsync_DoesNotExposeRequiredPhraseCheckToModelPrompt()
    {
        const string requiredPhrase = "hidden pass phrase";
        var modelClient = new CapturingModelClient();
        var handler = CreateHandler(
            datasetProvider: new FixedEvaluationDatasetProvider(
                new EvaluationDataset(
                    "leak-test-v1",
                    [
                        new EvaluationCase(
                            "case-1",
                            "Leak test",
                            "Answer from the available evidence.",
                            [new EvaluationCheck("required_phrase", Phrase: requiredPhrase)])
                    ])),
            modelClient: modelClient,
            vectorSearchStore: new CapturingVectorSearchStore { Chunks = [] });

        var result = await handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(DatasetVersion: "leak-test-v1", CorrelationId: "eval-no-leak"),
            CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        var request = Assert.Single(modelClient.Requests);
        var userMessage = request.Messages.Last(static message => message.Role == AiMessageRole.User).Content;
        Assert.DoesNotContain(requiredPhrase, userMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_RejectsInvalidDatasetBeforeCreatingRunOrCallingModel()
    {
        var modelClient = new CapturingModelClient();
        var runRepository = new CapturingEvaluationRunRepository();
        var handler = CreateHandler(
            datasetProvider: new FixedEvaluationDatasetProvider(
                new EvaluationDataset(
                    "invalid-dataset-v1",
                    [
                        new EvaluationCase(
                            "case-1",
                            "Invalid forbidden phrase",
                            "Question",
                            [new EvaluationCheck("forbidden_phrase", Phrase: " ")],
                            Context: "private answer content")
                    ])),
            modelClient: modelClient,
            runRepository: runRepository);

        var exception = await Assert.ThrowsAsync<EvaluationValidationException>(() =>
            handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
                new StartEvaluationRunCommand(DatasetVersion: "invalid-dataset-v1"),
                CancellationToken.None));

        Assert.Contains("invalid-dataset-v1", exception.Message);
        Assert.Contains("case-1", exception.Message);
        Assert.Contains("forbidden_phrase", exception.Message);
        Assert.DoesNotContain("private answer content", exception.Message);
        Assert.Empty(modelClient.Requests);
        Assert.Empty(runRepository.CaseResults);
        Assert.Null(runRepository.CompletedStatus);
    }

    [Fact]
    public async Task HandleAsync_StoresFailedCaseWhenModelProviderFails()
    {
        var runRepository = new CapturingEvaluationRunRepository();
        var handler = CreateHandler(
            modelClient: new ThrowingModelClient(),
            runRepository: runRepository);

        var result = await handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(CorrelationId: "eval-model-fails"),
            CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.All(result.Cases, evaluationCase =>
        {
            Assert.Equal("Failed", evaluationCase.Status);
            Assert.Equal("provider_unavailable", evaluationCase.ErrorCode);
        });
        Assert.Equal("Failed", runRepository.CompletedStatus);
    }

    [Fact]
    public async Task HandleAsync_StoresFailedCaseWhenRetrievalFails()
    {
        var handler = CreateHandler(
            datasetProvider: new FixedEvaluationDatasetProvider(
                CreateRetrievalBackedDataset("retrieval-failure-test-v1")),
            vectorSearchStore: new CapturingVectorSearchStore
            {
                SearchException = new RagVectorSearchException("postgres", "failed", "retrieval_query_failed")
            });

        var result = await handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(
                DatasetVersion: "retrieval-failure-test-v1",
                CorrelationId: "eval-retrieval-fails"),
            CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.All(result.Cases, evaluationCase =>
            Assert.Equal("retrieval_query_failed", evaluationCase.ErrorCode));
    }

    [Fact]
    public async Task HandleAsync_CancellationLeavesRunCanceledAndInspectable()
    {
        var runRepository = new CapturingEvaluationRunRepository();
        var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(
            datasetProvider: new FixedEvaluationDatasetProvider(
                CreateRetrievalBackedDataset("retrieval-canceled-test-v1")),
            runRepository: runRepository,
            vectorSearchStore: new CapturingVectorSearchStore
            {
                OnSearch = () => cancellation.Cancel()
            });

        var result = await handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(
                DatasetVersion: "retrieval-canceled-test-v1",
                CorrelationId: "eval-canceled"),
            cancellation.Token);

        Assert.Equal("Canceled", result.Status);
        Assert.Equal("Canceled", runRepository.CompletedStatus);
        Assert.NotEmpty(runRepository.CaseResults);
    }

    [Fact]
    public async Task HandleAsync_PostProviderCancellationPreservesLogCaseAndTerminalStatus()
    {
        var logRepository = new CapturingAiRequestLogRepository();
        var runRepository = new CapturingEvaluationRunRepository();
        var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(
            modelClient: new CancelAfterResponseModelClient(cancellation),
            logRepository: logRepository,
            runRepository: runRepository);

        var result = await handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(CorrelationId: "eval-post-provider-cancel"),
            cancellation.Token);

        Assert.Equal("Canceled", result.Status);
        Assert.Equal("Canceled", runRepository.CompletedStatus);
        Assert.NotEmpty(logRepository.Entries);
        Assert.NotEmpty(runRepository.CaseResults);
        Assert.Single(result.Cases);
    }

    [Fact]
    public async Task HandleAsync_OperationCanceledCasePersistenceMarksRunCanceled()
    {
        var cancellation = new CancellationTokenSource();
        var runRepository = new CapturingEvaluationRunRepository
        {
            OperationCanceledOnAddCaseResult = new OperationCanceledException(cancellation.Token)
        };
        var handler = CreateHandler(
            modelClient: new CancelAfterResponseModelClient(cancellation),
            runRepository: runRepository);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
                new StartEvaluationRunCommand(CorrelationId: "eval-case-persist-canceled"),
                cancellation.Token));

        Assert.Equal("Canceled", runRepository.CompletedStatus);
        Assert.DoesNotContain("Failed", runRepository.CompletionAttemptStatuses);
    }

    [Fact]
    public async Task HandleAsync_CanceledCompletionFailurePreservesOriginalCancellation()
    {
        var cancellation = new CancellationTokenSource();
        var cancellationException = new OperationCanceledException(cancellation.Token);
        var runRepository = new CapturingEvaluationRunRepository
        {
            OperationCanceledOnAddCaseResult = cancellationException,
            ThrowOnCompleteRunAttempts = 1
        };
        var handler = CreateHandler(
            modelClient: new CancelAfterResponseModelClient(cancellation),
            runRepository: runRepository);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
                new StartEvaluationRunCommand(CorrelationId: "eval-canceled-complete-fails"),
                cancellation.Token));

        Assert.Same(cancellationException, exception);
        Assert.Null(runRepository.CompletedStatus);
        Assert.Equal(["Canceled"], runRepository.CompletionAttemptStatuses);
    }

    [Fact]
    public async Task HandleAsync_CasePersistenceFailureMarksRunFailed()
    {
        var logRepository = new CapturingAiRequestLogRepository();
        var runRepository = new CapturingEvaluationRunRepository
        {
            ThrowOnAddCaseResult = true
        };
        var handler = CreateHandler(logRepository: logRepository, runRepository: runRepository);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
                new StartEvaluationRunCommand(CorrelationId: "eval-case-persist-fails"),
                CancellationToken.None));

        Assert.NotEmpty(logRepository.Entries);
        Assert.Equal("Failed", runRepository.CompletedStatus);
    }

    [Fact]
    public async Task HandleAsync_CasePersistenceFailurePreservesOriginalExceptionWhenFailedFallbackFails()
    {
        var runRepository = new CapturingEvaluationRunRepository
        {
            AddCaseResultException = new InvalidOperationException("case failed"),
            CompleteRunExceptions = [new InvalidOperationException("db down")]
        };
        var handler = CreateHandler(runRepository: runRepository);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
                new StartEvaluationRunCommand(CorrelationId: "eval-case-persist-and-complete-fail"),
                CancellationToken.None));

        Assert.Equal("case failed", exception.Message);
        Assert.DoesNotContain("db down", exception.Message);
        Assert.Null(runRepository.CompletedStatus);
        Assert.Equal(["Failed"], runRepository.CompletionAttemptStatuses);
    }

    [Fact]
    public async Task HandleAsync_FinalCompletionFailureRecoversTerminalFailedStatus()
    {
        var runRepository = new CapturingEvaluationRunRepository
        {
            ThrowOnCompleteRunAttempts = 1
        };
        var handler = CreateHandler(runRepository: runRepository);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
                new StartEvaluationRunCommand(CorrelationId: "eval-final-complete-fails"),
                CancellationToken.None));

        Assert.NotEmpty(runRepository.CaseResults);
        Assert.Equal("Failed", runRepository.CompletedStatus);
    }

    [Fact]
    public async Task HandleAsync_FinalCompletionFailurePreservesOriginalExceptionWhenFailedFallbackFails()
    {
        var runRepository = new CapturingEvaluationRunRepository
        {
            CompleteRunExceptions =
            [
                new InvalidOperationException("completion failed"),
                new InvalidOperationException("db down")
            ]
        };
        var handler = CreateHandler(runRepository: runRepository);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
                new StartEvaluationRunCommand(CorrelationId: "eval-final-complete-and-fallback-fail"),
                CancellationToken.None));

        Assert.Equal("completion failed", exception.Message);
        Assert.DoesNotContain("db down", exception.Message);
        Assert.NotEmpty(runRepository.CaseResults);
        Assert.Null(runRepository.CompletedStatus);
        Assert.Equal(["Succeeded", "Failed"], runRepository.CompletionAttemptStatuses);
    }

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
