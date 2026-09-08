using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Exceptions;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Generation.Chat;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Templates;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Observability;

namespace GenAIPlatform.UnitTests;

public sealed partial class RagChatHandlerTests
{
    [Fact]
    public async Task HandleAsync_SearchesAllowedContextAndCallsModelWithCitations()
    {
        var modelClient = new CapturingModelClient();
        var embeddingClient = new CapturingEmbeddingClient([1f, 0f]);
        var firstChunk = CreateRetrievedChunk("Architecture notes", similarityScore: 0.93);
        var secondChunk = CreateRetrievedChunk("Security notes", similarityScore: 0.88, position: 1);
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results =
            [
                firstChunk,
                secondChunk
            ]
        };
        var documentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var handler = CreateHandler(
            modelClient,
            embeddingClient,
            vectorSearchStore);

        var response = await handler.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand(
                "How does retrieval work?",
                TopK: 2,
                MinSimilarityScore: 0.5,
                DocumentIds: [documentId],
                CorrelationId: "rag-test"),
            CancellationToken.None);

        Assert.False(response.NoContext);
        Assert.Equal("rag answer", response.Message);
        Assert.Equal("test-model", response.Model);
        Assert.Equal("fake", response.Provider);
        Assert.Equal("rag-test", response.CorrelationId);
        Assert.Equal(RagChatPrompt.TemplateName, response.Prompt?.TemplateName);
        Assert.Collection(
            response.Citations,
            citation =>
            {
                Assert.Equal("1", citation.ReferenceId);
                Assert.Equal(firstChunk.DocumentId, citation.DocumentId);
                Assert.Equal(firstChunk.ChunkId, citation.ChunkId);
                Assert.Equal("Architecture notes", citation.Title);
                Assert.Equal(0.93, citation.SimilarityScore);
            },
            citation =>
            {
                Assert.Equal("2", citation.ReferenceId);
                Assert.Equal(secondChunk.DocumentId, citation.DocumentId);
                Assert.Equal(secondChunk.ChunkId, citation.ChunkId);
            });

        Assert.NotNull(embeddingClient.Request);
        Assert.Equal("How does retrieval work?", embeddingClient.Request.Input);
        Assert.Equal("test-embedding", embeddingClient.Request.Model);
        Assert.Equal("rag-test", embeddingClient.Request.CorrelationId);

        Assert.NotNull(vectorSearchStore.Query);
        Assert.Equal("tenant-a", vectorSearchStore.Query.TenantId);
        Assert.Equal("alice", vectorSearchStore.Query.UserId);
        Assert.Equal("test-embedding", vectorSearchStore.Query.EmbeddingModel);
        Assert.Equal("fake", vectorSearchStore.Query.EmbeddingProvider);
        Assert.Equal(2, vectorSearchStore.Query.TopK);
        Assert.Equal(0.5, vectorSearchStore.Query.MinSimilarityScore);
        Assert.Equal([documentId], vectorSearchStore.Query.DocumentIds);

        Assert.NotNull(modelClient.Request);
        Assert.Equal("test-model", modelClient.Request.Model);
        Assert.Equal(RagChatPrompt.TemplateName, modelClient.Request.Prompt?.TemplateName);
        Assert.Contains("How does retrieval work?", modelClient.Request.Messages[1].Content);
        Assert.Contains("[1]", modelClient.Request.Messages[1].Content);
        Assert.Contains("Architecture notes", modelClient.Request.Messages[1].Content);
        Assert.DoesNotContain(firstChunk.DocumentId.ToString("D"), modelClient.Request.Messages[1].Content);
        Assert.DoesNotContain(firstChunk.ChunkId.ToString("D"), modelClient.Request.Messages[1].Content);
        Assert.DoesNotContain(secondChunk.DocumentId.ToString("D"), modelClient.Request.Messages[1].Content);
        Assert.DoesNotContain(secondChunk.ChunkId.ToString("D"), modelClient.Request.Messages[1].Content);
    }

    [Fact]
    public async Task HandleAsync_ReturnsFallbackWithoutModelCallWhenNoContextIsFound()
    {
        var modelClient = new CapturingModelClient();
        var embeddingClient = new CapturingEmbeddingClient([1f, 0f]);
        var vectorSearchStore = new CapturingVectorSearchStore();
        var logRepository = new CapturingAiRequestLogRepository();
        var handler = CreateHandler(
            modelClient,
            embeddingClient,
            vectorSearchStore,
            requestLogRepository: logRepository,
            pricingRepository: new InMemoryPricingRepository([
                new PricingRecord(
                    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    "fake",
                    "test-embedding",
                    "USD",
                    InputTokenPricePerMillion: 0,
                    OutputTokenPricePerMillion: 0,
                    EmbeddingTokenPricePerMillion: 100m,
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    EffectiveToUtc: null)
            ]));

        var response = await handler.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand("What is not in the documents?", CorrelationId: "no-context"),
            CancellationToken.None);

        Assert.True(response.NoContext);
        Assert.Equal("No matching context.", response.Message);
        Assert.Equal("test-model", response.Model);
        Assert.Null(response.Provider);
        Assert.Null(response.Prompt);
        Assert.Empty(response.Citations);
        Assert.Null(modelClient.Request);

        Assert.NotNull(embeddingClient.Request);
        Assert.NotNull(vectorSearchStore.Query);

        var entry = Assert.Single(logRepository.Entries);
        Assert.Equal("v1", entry.ApiVersion);
        Assert.Equal("alice", entry.UserId);
        Assert.Equal("tenant-a", entry.TenantId);
        Assert.Equal("no-context", entry.CorrelationId);
        Assert.Equal("Succeeded", entry.Status);
        Assert.Null(entry.ErrorCode);
        Assert.Equal("no-model", entry.Provider);
        Assert.Equal("test-model", entry.Model);
        Assert.Null(entry.InputTokens);
        Assert.Null(entry.OutputTokens);
        Assert.Null(entry.TotalTokens);
        Assert.Equal(4, entry.EmbeddingTokens);
        Assert.Equal(0.00040000m, entry.EstimatedCost);
        Assert.Equal("USD", entry.CostCurrency);
        Assert.Null(entry.Prompt);
        Assert.NotNull(entry.RetrievalLatency);
        Assert.Empty(entry.RetrievedDocuments);
    }

    [Fact]
    public async Task HandleAsync_LogsFallbackWhenPromptBudgetRemovesAllCitations()
    {
        var modelClient = new CapturingModelClient();
        var logRepository = new CapturingAiRequestLogRepository();
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results =
            [
                CreateRetrievedChunk(
                    "Budgeted out",
                    0.94,
                    text: "This text cannot fit because the context budget is smaller than the citation prefix.")
            ]
        };
        var handler = CreateHandler(
            modelClient,
            new CapturingEmbeddingClient([1f, 0f]),
            vectorSearchStore,
            ragOptions: new RagOptions
            {
                DefaultTopK = 3,
                MaxTopK = 10,
                DefaultMinSimilarityScore = 0.2,
                MaxDocumentFilters = 5,
                MaxContextCharacters = 1,
                NoContextFallbackMessage = "No matching context."
            },
            requestLogRepository: logRepository);

        var response = await handler.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand("Use context that will be trimmed.", CorrelationId: "trimmed-context"),
            CancellationToken.None);

        Assert.True(response.NoContext);
        Assert.Equal("No matching context.", response.Message);
        Assert.Null(modelClient.Request);

        var entry = Assert.Single(logRepository.Entries);
        Assert.Equal("trimmed-context", entry.CorrelationId);
        Assert.Equal("Succeeded", entry.Status);
        Assert.Equal("no-model", entry.Provider);
        Assert.Equal("test-model", entry.Model);
        Assert.Equal(4, entry.EmbeddingTokens);
        Assert.Null(entry.Prompt);
        Assert.NotNull(entry.RetrievalLatency);
        Assert.Empty(entry.RetrievedDocuments);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("nan")]
    public async Task HandleAsync_RejectsInvalidQueryEmbeddingBeforeSearchOrModel(string vectorShape)
    {
        var modelClient = new CapturingModelClient();
        var embeddingClient = new CapturingEmbeddingClient(vectorShape == "zero"
            ? [0f, 0f]
            : [float.NaN, 1f]);
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results = [CreateRetrievedChunk("Unused", 0.9)]
        };
        var handler = CreateHandler(
            modelClient,
            embeddingClient,
            vectorSearchStore);

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(
                new RagChatCommand("Reject invalid embedding.", CorrelationId: "bad-vector"),
                CancellationToken.None));

        Assert.Equal("invalid_embedding", exception.ErrorCode);
        Assert.NotNull(embeddingClient.Request);
        Assert.Null(vectorSearchStore.Query);
        Assert.Null(modelClient.Request);
    }

    [Theory]
    [InlineData("null-vector")]
    [InlineData("blank-model")]
    [InlineData("blank-provider")]
    [InlineData("negative-tokens")]
    public async Task HandleAsync_RejectsMalformedEmbeddingMetadataBeforeSearchOrModel(string responseShape)
    {
        var modelClient = new CapturingModelClient();
        var embeddingClient = responseShape switch
        {
            "null-vector" => new CapturingEmbeddingClient(null),
            "blank-model" => new CapturingEmbeddingClient([1f, 0f], model: " "),
            "blank-provider" => new CapturingEmbeddingClient([1f, 0f], provider: " "),
            "negative-tokens" => new CapturingEmbeddingClient([1f, 0f], inputTokens: -1),
            _ => throw new InvalidOperationException($"Unknown response shape '{responseShape}'.")
        };
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results = [CreateRetrievedChunk("Unused", 0.9)]
        };
        var handler = CreateHandler(
            modelClient,
            embeddingClient,
            vectorSearchStore);

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(
                new RagChatCommand("Reject malformed metadata.", CorrelationId: "bad-metadata"),
                CancellationToken.None));

        Assert.Equal("invalid_embedding", exception.ErrorCode);
        Assert.NotNull(embeddingClient.Request);
        Assert.Null(vectorSearchStore.Query);
        Assert.Null(modelClient.Request);
    }

    [Fact]
    public async Task HandleAsync_RejectsTopKOutsidePolicy()
    {
        var handler = CreateHandler(
            new CapturingModelClient(),
            new CapturingEmbeddingClient([1f, 0f]),
            new CapturingVectorSearchStore());

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(
                new RagChatCommand("Use too many chunks.", TopK: 50),
                CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_UsesDefaultSimilarityThresholdWhenNoOverrideIsSupplied()
    {
        var vectorSearchStore = new CapturingVectorSearchStore();
        var handler = CreateHandler(
            new CapturingModelClient(),
            new CapturingEmbeddingClient([1f, 0f]),
            vectorSearchStore);

        await handler.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand("Use the default threshold."),
            CancellationToken.None);

        Assert.NotNull(vectorSearchStore.Query);
        Assert.Equal(0.2, vectorSearchStore.Query.MinSimilarityScore);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.01)]
    [InlineData(1.01)]
    public async Task HandleAsync_RejectsInvalidSimilarityThresholdBeforeEmbeddingOrSearch(
        double minSimilarityScore)
    {
        var modelClient = new CapturingModelClient();
        var embeddingClient = new CapturingEmbeddingClient([1f, 0f]);
        var vectorSearchStore = new CapturingVectorSearchStore();
        var handler = CreateHandler(
            modelClient,
            embeddingClient,
            vectorSearchStore);

        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(
                new RagChatCommand(
                    "Use an invalid threshold.",
                    MinSimilarityScore: minSimilarityScore),
                CancellationToken.None));

        Assert.Equal("Minimum similarity score must be between -1 and 1.", Assert.Single(exception.Failures).ErrorMessage);
        Assert.Null(embeddingClient.Request);
        Assert.Null(vectorSearchStore.Query);
        Assert.Null(modelClient.Request);
    }

    [Fact]
    public async Task HandleAsync_DeduplicatesDocumentFilters()
    {
        var vectorSearchStore = new CapturingVectorSearchStore();
        var documentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var handler = CreateHandler(
            new CapturingModelClient(),
            new CapturingEmbeddingClient([1f, 0f]),
            vectorSearchStore);

        await handler.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand(
                "Use a filtered document.",
                DocumentIds: [documentId, documentId]),
            CancellationToken.None);

        Assert.NotNull(vectorSearchStore.Query);
        Assert.Equal([documentId], vectorSearchStore.Query.DocumentIds);
    }

    [Fact]
    public async Task HandleAsync_RejectsEmptyDocumentFiltersBeforeEmbeddingOrSearch()
    {
        var embeddingClient = new CapturingEmbeddingClient([1f, 0f]);
        var vectorSearchStore = new CapturingVectorSearchStore();
        var modelClient = new CapturingModelClient();
        var handler = CreateHandler(
            modelClient,
            embeddingClient,
            vectorSearchStore);
        var command = new RagChatCommand(
            "Use selected documents.",
            DocumentIds: []);

        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(command, CancellationToken.None));

        Assert.Equal("DocumentIds must be omitted or contain at least one id.", Assert.Single(exception.Failures).ErrorMessage);
        Assert.Null(embeddingClient.Request);
        Assert.Null(vectorSearchStore.Query);
        Assert.Null(modelClient.Request);
    }

    [Fact]
    public async Task HandleAsync_RejectsRawDocumentFilterCountBeforeDeduplication()
    {
        var embeddingClient = new CapturingEmbeddingClient([1f, 0f]);
        var vectorSearchStore = new CapturingVectorSearchStore();
        var documentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var handler = CreateHandler(
            new CapturingModelClient(),
            embeddingClient,
            vectorSearchStore,
            ragOptions: new RagOptions
            {
                DefaultTopK = 3,
                MaxTopK = 10,
                DefaultMinSimilarityScore = 0.2,
                MaxDocumentFilters = 2,
                MaxContextCharacters = 6000,
                NoContextFallbackMessage = "No matching context."
            });

        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(
                new RagChatCommand(
                    "Use a filtered document.",
                    DocumentIds: [documentId, documentId, documentId]),
                CancellationToken.None));

        Assert.Equal("DocumentIds must contain 2 ids or fewer.", Assert.Single(exception.Failures).ErrorMessage);
        Assert.Null(embeddingClient.Request);
        Assert.Null(vectorSearchStore.Query);
    }

    [Fact]
    public async Task HandleAsync_RejectsEmptyDocumentIdFilterBeforeEmbeddingOrSearch()
    {
        var embeddingClient = new CapturingEmbeddingClient([1f, 0f]);
        var vectorSearchStore = new CapturingVectorSearchStore();
        var handler = CreateHandler(
            new CapturingModelClient(),
            embeddingClient,
            vectorSearchStore);

        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(
                new RagChatCommand(
                    "Use an invalid filter.",
                    DocumentIds: [Guid.Empty]),
                CancellationToken.None));

        Assert.Equal("DocumentIds must not contain empty GUID values.", Assert.Single(exception.Failures).ErrorMessage);
        Assert.Null(embeddingClient.Request);
        Assert.Null(vectorSearchStore.Query);
    }

    [Fact]
    public async Task HandleAsync_ChecksRetrievalReadinessBeforeEmbeddingOrModelCalls()
    {
        var modelClient = new CapturingModelClient();
        var embeddingClient = new CapturingEmbeddingClient([1f, 0f]);
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            ReadinessException = new RagVectorSearchException(
                "postgres",
                "RAG retrieval schema is not ready.",
                errorCode: "retrieval_schema_error")
        };
        var handler = CreateHandler(
            modelClient,
            embeddingClient,
            vectorSearchStore);

        var exception = await Assert.ThrowsAsync<RagVectorSearchException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(
                new RagChatCommand("Use retrieval readiness."),
                CancellationToken.None));

        Assert.Equal("retrieval_schema_error", exception.ErrorCode);
        Assert.Equal(1, vectorSearchStore.ReadinessCalls);
        Assert.Null(embeddingClient.Request);
        Assert.Null(vectorSearchStore.Query);
        Assert.Null(modelClient.Request);
    }

    [Fact]
    public async Task HandleAsync_RejectsQuestionOverEmbeddingLimitBeforeEmbeddingOrSearch()
    {
        var modelClient = new CapturingModelClient();
        var embeddingClient = new CapturingEmbeddingClient([1f, 0f]);
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results = [CreateRetrievedChunk("Unused", 0.9)]
        };
        var handler = CreateHandler(
            modelClient,
            embeddingClient,
            vectorSearchStore,
            embeddingOptions: new EmbeddingOptions
            {
                DefaultModel = "test-embedding",
                MaxInputCharacters = 10
            },
            modelGatewayOptions: new ModelGatewayOptions
            {
                DefaultModel = "test-model",
                StrongModel = "test-model-strong",
                CheapModel = "test-model-cheap",
                EvaluationModel = "test-model-evaluation",
                DefaultTemperature = 0.3,
                DefaultMaxOutputTokens = 256,
                MaxOutputTokensLimit = 512,
                MaxInputMessageCharacters = 100
            });

        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(
                new RagChatCommand("This question is too long."),
                CancellationToken.None));

        Assert.Equal("RAG message must be 10 characters or fewer.", Assert.Single(exception.Failures).ErrorMessage);
        Assert.Null(embeddingClient.Request);
        Assert.Null(vectorSearchStore.Query);
        Assert.Null(modelClient.Request);
    }

    [Fact]
    public async Task HandleAsync_LimitsContextAndCitationsToConfiguredBudget()
    {
        var modelClient = new CapturingModelClient();
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results =
            [
                CreateRetrievedChunk("First notes", 0.93, text: new string('a', 1000)),
                CreateRetrievedChunk("Second notes", 0.91, position: 1, text: new string('b', 1000))
            ]
        };
        var handler = CreateHandler(
            modelClient,
            new CapturingEmbeddingClient([1f, 0f]),
            vectorSearchStore,
            ragOptions: new RagOptions
            {
                DefaultTopK = 3,
                MaxTopK = 10,
                DefaultMinSimilarityScore = 0.2,
                MaxDocumentFilters = 5,
                MaxContextCharacters = 500,
                NoContextFallbackMessage = "No matching context."
            });

        var response = await handler.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand("Keep the prompt bounded."),
            CancellationToken.None);

        Assert.False(response.NoContext);
        var citation = Assert.Single(response.Citations);
        Assert.Equal("First notes", citation.Title);
        Assert.NotNull(modelClient.Request);

        var userMessage = modelClient.Request.Messages.Single(static message => message.Role == AiMessageRole.User).Content;
        var contextText = userMessage.Split("Document context:\n", StringSplitOptions.None)[1];

        Assert.True(contextText.Length <= 500);
        Assert.Contains("First notes", userMessage);
        Assert.DoesNotContain("Second notes", userMessage);
    }

    [Fact]
    public async Task HandleAsync_AssignsCitationIdsInRetrievalOrderWhenScoresTie()
    {
        var firstChunk = CreateRetrievedChunk("First tied notes", 0.9);
        var secondChunk = CreateRetrievedChunk("Second tied notes", 0.9, position: 1);
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results = [firstChunk, secondChunk]
        };
        var handler = CreateHandler(
            new CapturingModelClient(),
            new CapturingEmbeddingClient([1f, 0f]),
            vectorSearchStore);

        var response = await handler.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand("Use tied context."),
            CancellationToken.None);

        Assert.Collection(
            response.Citations,
            citation =>
            {
                Assert.Equal("1", citation.ReferenceId);
                Assert.Equal(firstChunk.ChunkId, citation.ChunkId);
            },
            citation =>
            {
                Assert.Equal("2", citation.ReferenceId);
                Assert.Equal(secondChunk.ChunkId, citation.ChunkId);
            });
    }

    [Fact]
    public async Task HandleAsync_BudgetsContextAgainstRenderedPromptLimit()
    {
        var modelClient = new CapturingModelClient();
        var maxInputMessageCharacters = 1500;
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results =
            [
                CreateRetrievedChunk("Budget notes", 0.94, text: new string('a', 1000)),
                CreateRetrievedChunk("Overflow notes", 0.91, position: 1, text: new string('b', 1000))
            ]
        };
        var handler = CreateHandler(
            modelClient,
            new CapturingEmbeddingClient([1f, 0f]),
            vectorSearchStore,
            ragOptions: new RagOptions
            {
                DefaultTopK = 3,
                MaxTopK = 10,
                DefaultMinSimilarityScore = 0.2,
                MaxDocumentFilters = 5,
                MaxContextCharacters = 600,
                NoContextFallbackMessage = "No matching context."
            },
            modelGatewayOptions: new ModelGatewayOptions
            {
                DefaultModel = "test-model",
                StrongModel = "test-model-strong",
                CheapModel = "test-model-cheap",
                EvaluationModel = "test-model-evaluation",
                DefaultTemperature = 0.3,
                DefaultMaxOutputTokens = 256,
                MaxOutputTokensLimit = 512,
                MaxInputMessageCharacters = maxInputMessageCharacters
            });

        var response = await handler.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand(new string('q', 760)),
            CancellationToken.None);

        Assert.False(response.NoContext);
        Assert.NotNull(modelClient.Request);
        var userMessage = modelClient.Request.Messages.Single(static message => message.Role == AiMessageRole.User).Content;
        var contextText = userMessage.Split("Document context:\n", StringSplitOptions.None)[1];

        Assert.True(CountModelInputCharacters(modelClient.Request) <= maxInputMessageCharacters);
        Assert.True(contextText.Length < 600);
        Assert.Contains("Budget notes", userMessage);
        Assert.DoesNotContain("Overflow notes", userMessage);
    }

    [Fact]
    public async Task HandleAsync_BudgetsContextAgainstLongSystemInstructions()
    {
        var modelClient = new CapturingModelClient();
        var maxInputMessageCharacters = 850;
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results =
            [
                CreateRetrievedChunk("System budget notes", 0.94, text: new string('a', 1000)),
                CreateRetrievedChunk("Overflow notes", 0.91, position: 1, text: new string('b', 1000))
            ]
        };
        var handler = CreateHandler(
            modelClient,
            new CapturingEmbeddingClient([1f, 0f]),
            vectorSearchStore,
            ragOptions: new RagOptions
            {
                DefaultTopK = 3,
                MaxTopK = 10,
                DefaultMinSimilarityScore = 0.2,
                MaxDocumentFilters = 5,
                MaxContextCharacters = 500,
                NoContextFallbackMessage = "No matching context."
            },
            modelGatewayOptions: new ModelGatewayOptions
            {
                DefaultModel = "test-model",
                StrongModel = "test-model-strong",
                CheapModel = "test-model-cheap",
                EvaluationModel = "test-model-evaluation",
                DefaultTemperature = 0.3,
                DefaultMaxOutputTokens = 256,
                MaxOutputTokensLimit = 512,
                MaxInputMessageCharacters = maxInputMessageCharacters
            },
            promptTemplate: CreateRagPromptTemplate(new string('s', 300)));

        var response = await handler.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand(new string('q', 100)),
            CancellationToken.None);

        Assert.False(response.NoContext);
        Assert.NotNull(modelClient.Request);
        var userMessage = modelClient.Request.Messages.Single(static message => message.Role == AiMessageRole.User).Content;
        var contextText = userMessage.Split("Document context:\n", StringSplitOptions.None)[1];

        Assert.True(CountModelInputCharacters(modelClient.Request) <= maxInputMessageCharacters);
        Assert.True(contextText.Length < 500);
        Assert.Contains("System budget notes", userMessage);
        Assert.DoesNotContain("Overflow notes", userMessage);
    }

    [Fact]
    public async Task HandleAsync_DoesNotReturnNoContextWhenRetrievedMetadataIsOverlong()
    {
        var modelClient = new CapturingModelClient();
        var title = new string('t', 500);
        var fileName = new string('f', 500) + ".md";
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results =
            [
                CreateRetrievedChunk(
                    title,
                    0.94,
                    text: "Relevant context.",
                    fileName: fileName)
            ]
        };
        var handler = CreateHandler(
            modelClient,
            new CapturingEmbeddingClient([1f, 0f]),
            vectorSearchStore,
            ragOptions: new RagOptions
            {
                DefaultTopK = 3,
                MaxTopK = 10,
                DefaultMinSimilarityScore = 0.2,
                MaxDocumentFilters = 5,
                MaxContextCharacters = 500,
                NoContextFallbackMessage = "No matching context."
            });

        var response = await handler.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand("Use the long-metadata document."),
            CancellationToken.None);

        Assert.False(response.NoContext);
        var citation = Assert.Single(response.Citations);
        Assert.Equal(title, citation.Title);
        Assert.Equal(fileName, citation.FileName);
        Assert.NotNull(modelClient.Request);
        var userMessage = modelClient.Request.Messages.Single(static message => message.Role == AiMessageRole.User).Content;
        Assert.Contains("Relevant context.", userMessage);
        Assert.DoesNotContain(new string('t', 200), userMessage);
        Assert.DoesNotContain(new string('f', 200), userMessage);
    }

    [Fact]
    public async Task HandleAsync_RejectsQuestionWithoutContextBudgetBeforeEmbeddingOrSearch()
    {
        var embeddingClient = new CapturingEmbeddingClient([1f, 0f]);
        var vectorSearchStore = new CapturingVectorSearchStore
        {
            Results = [CreateRetrievedChunk("Unused", 0.9)]
        };
        var handler = CreateHandler(
            new CapturingModelClient(),
            embeddingClient,
            vectorSearchStore,
            modelGatewayOptions: new ModelGatewayOptions
            {
                DefaultModel = "test-model",
                StrongModel = "test-model-strong",
                CheapModel = "test-model-cheap",
                EvaluationModel = "test-model-evaluation",
                DefaultTemperature = 0.3,
                DefaultMaxOutputTokens = 256,
                MaxOutputTokensLimit = 512,
                MaxInputMessageCharacters = 40
            });

        await Assert.ThrowsAsync<ModelRequestValidationException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(
                new RagChatCommand(new string('q', 35)),
                CancellationToken.None));

        Assert.Null(embeddingClient.Request);
        Assert.Null(vectorSearchStore.Query);
    }

    [Fact]
    public async Task HandleAsync_RequiresAuthenticatedTenant()
    {
        var handler = CreateHandler(
            new CapturingModelClient(),
            new CapturingEmbeddingClient([1f, 0f]),
            new CapturingVectorSearchStore(),
            new FakeUserContext("alice", tenantId: null));

        await Assert.ThrowsAsync<UnauthorizedRequestException>(() =>
            handler.DispatchAsync<RagChatCommand, RagChatResponse>(
                new RagChatCommand("Question"),
                CancellationToken.None));
    }

}
