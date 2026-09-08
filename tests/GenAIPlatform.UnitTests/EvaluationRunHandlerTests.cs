using GenAIPlatform.Application.Core.Configuration;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Evaluations.StartRun;
using GenAIPlatform.Application.Generation.Chat;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Evaluations;
using GenAIPlatform.Domain.Exceptions;

namespace GenAIPlatform.UnitTests;

public sealed partial class EvaluationRunHandlerTests
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

}
