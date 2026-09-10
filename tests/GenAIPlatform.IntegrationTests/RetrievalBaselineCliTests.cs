using System.Text.Json;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Evaluations.Retrieval;
using GenAIPlatform.Evaluations;
using GenAIPlatform.Evaluations.RetrievalBaseline;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GenAIPlatform.IntegrationTests;

public sealed class RetrievalBaselineCliTests
{
    private const string QuestionSentinel = "zzqsentinelquestionzz";
    private const string ChunkSentinel = "zzqsentinelchunkzz";

    [Fact]
    public void BothVerbsAreAcceptedAndAnythingElseIsAUsageError()
    {
        Assert.True(EvaluationCliVerbs.IsKnown(EvaluationCliVerbs.Run));
        Assert.True(EvaluationCliVerbs.IsKnown(EvaluationCliVerbs.RetrievalBaseline));
        Assert.False(EvaluationCliVerbs.IsKnown("baseline"));
        Assert.False(EvaluationCliVerbs.IsKnown(null));
        Assert.Contains("run", EvaluationCliVerbs.UsageText, StringComparison.Ordinal);
        Assert.Contains("retrieval-baseline", EvaluationCliVerbs.UsageText, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsFallBackToTheDefaultOutputPathAndUnknownRevision()
    {
        var parsed = RetrievalBaselineCliOptionsParser.Parse([], revisionFromEnvironment: null);

        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Options);
        Assert.Equal(RetrievalBaselineCliOptionsParser.DefaultOutputPath, parsed.Options.OutputPath);
        Assert.Equal("unknown", parsed.Options.CodeRevision);
    }

    [Fact]
    public void OptionsUseTheEnvironmentRevisionWhenNoRevisionIsPassed()
    {
        var parsed = RetrievalBaselineCliOptionsParser.Parse([], "1937d92");

        Assert.Equal("1937d92", parsed.Options!.CodeRevision);
    }

    [Fact]
    public void ExplicitOptionsWinOverTheEnvironmentRevision()
    {
        var parsed = RetrievalBaselineCliOptionsParser.Parse(
            ["--output", "reports/baseline.json", "--revision", "abc123"],
            "1937d92");

        Assert.Equal("reports/baseline.json", parsed.Options!.OutputPath);
        Assert.Equal("abc123", parsed.Options.CodeRevision);
    }

    [Theory]
    [InlineData("--output")]
    [InlineData("--revision")]
    public void AnOptionWithoutAValueIsAUsageError(string option)
    {
        var missingValue = RetrievalBaselineCliOptionsParser.Parse([option], revisionFromEnvironment: null);
        var followedByOption = RetrievalBaselineCliOptionsParser.Parse(
            [option, "--revision", "abc"],
            revisionFromEnvironment: null);

        Assert.Null(missingValue.Options);
        Assert.Contains(option, missingValue.Error!, StringComparison.Ordinal);
        Assert.Null(followedByOption.Options);
    }

    [Fact]
    public void ConfigurationOverridesArePassedThroughInsteadOfBeingRejected()
    {
        var parsed = RetrievalBaselineCliOptionsParser.Parse(
            ["--GenAIPlatform:Embeddings:MockVariant=Lexical"],
            revisionFromEnvironment: null);

        Assert.Null(parsed.Error);
        Assert.Equal(RetrievalBaselineCliOptionsParser.DefaultOutputPath, parsed.Options!.OutputPath);
    }

    [Fact]
    public void HostConfigurationDropsTheVerbAndItsOwnOptionsButKeepsConfigurationOverrides()
    {
        var contentRoot = Directory.CreateTempSubdirectory("genai-baseline-cli-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"), "{}");

            var builder = global::EvaluationCliHost.CreateBuilder(
                [
                    "retrieval-baseline",
                    "--output",
                    "report.json",
                    "--revision",
                    "abc123",
                    "--GenAIPlatform:Embeddings:MockVariant=Lexical"
                ],
                contentRoot);

            Assert.Equal("Lexical", builder.Configuration["GenAIPlatform:Embeddings:MockVariant"]);
            Assert.Null(builder.Configuration["output"]);
            Assert.Null(builder.Configuration["revision"]);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void ExitCodeIsZeroWhenEveryGateIsMetAndOneWhenAGateFails()
    {
        Assert.Equal(
            RetrievalBaselineCliRunner.GatesMetExitCode,
            RetrievalBaselineCliRunner.GetExitCode(Report(gatesPassed: true)));
        Assert.Equal(
            RetrievalBaselineCliRunner.GateFailedExitCode,
            RetrievalBaselineCliRunner.GetExitCode(Report(gatesPassed: false)));
    }

    [Fact]
    public async Task ReportAndSummaryCarryOnlyIdsHashesSettingsAndMetrics()
    {
        var report = Report(gatesPassed: true);
        var outputPath = Path.Combine(
            Directory.CreateTempSubdirectory("genai-baseline-report-").FullName,
            "nested",
            "retrieval-baseline.json");
        var summary = new StringWriter();

        await RetrievalBaselineCliRunner.WriteReportAsync(
            report,
            outputPath,
            TestContext.Current.CancellationToken);
        RetrievalBaselineCliRunner.WriteSummary(report, outputPath, summary);

        var written = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        var roundTripped = JsonSerializer.Deserialize<RetrievalBaselineReport>(
            written,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(report.DatasetVersion, roundTripped!.DatasetVersion);
        Assert.Equal(report.Aggregates.RecallAtK, roundTripped.Aggregates.RecallAtK);
        Assert.DoesNotContain("Password", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recall_at_k", summary.ToString(), StringComparison.Ordinal);
        Assert.Contains("all met", summary.ToString(), StringComparison.Ordinal);

        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(outputPath))!, recursive: true);
    }

    [Fact]
    public async Task AMissingConnectionStringExitsWithOneAndNoConnectionDetails()
    {
        using var provider = BuildProvider(configureStore: null);
        using var scope = provider.CreateScope();
        var output = new StringWriter();
        var error = new StringWriter();
        var outputPath = Path.Combine(
            Directory.CreateTempSubdirectory("genai-baseline-unconfigured-").FullName,
            "retrieval-baseline.json");

        var exitCode = await RetrievalBaselineCliRunner.RunAsync(
            scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>(),
            new RetrievalBaselineCliOptions(outputPath, "abc123"),
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(RetrievalBaselineCliRunner.StoreFailedExitCode, exitCode);
        Assert.Equal(1, exitCode);
        var message = error.ToString();
        Assert.Contains("Retrieval baseline store is not configured.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Username", message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outputPath));
        Assert.Empty(output.ToString());

        Directory.Delete(Path.GetDirectoryName(outputPath)!, recursive: true);
    }

    [Fact]
    public async Task NeitherTheReportNorTheConsoleRepeatsQuestionOrChunkText()
    {
        var store = new RecordingCorpusStore();
        using var provider = BuildProvider(store);
        using var scope = provider.CreateScope();
        var output = new StringWriter();
        var outputPath = Path.Combine(
            Directory.CreateTempSubdirectory("genai-baseline-sanitization-").FullName,
            "retrieval-baseline.json");

        var exitCode = await RetrievalBaselineCliRunner.RunAsync(
            scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>(),
            new RetrievalBaselineCliOptions(outputPath, "abc123"),
            output,
            new StringWriter(),
            TestContext.Current.CancellationToken);

        var written = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        var console = output.ToString();

        // The sentinels did reach the run: they were embedded, stored and retrieved.
        var corpus = store.Corpus;
        Assert.NotNull(corpus);
        Assert.Contains(corpus.Documents, document => document.Chunks.Any(chunk =>
            chunk.Text.Contains(ChunkSentinel, StringComparison.Ordinal)));
        Assert.Contains("q-sentinel-relevant", written, StringComparison.Ordinal);
        Assert.Contains("doc-sentinel", written, StringComparison.Ordinal);

        foreach (var sentinel in new[] { QuestionSentinel, ChunkSentinel })
        {
            Assert.DoesNotContain(sentinel, written, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sentinel, console, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("\"embedding\"", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"question\"", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"text\"", written, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(exitCode, 0, 1);

        Directory.Delete(Path.GetDirectoryName(outputPath)!, recursive: true);
    }

    [Fact]
    public async Task ARunCompletesWithoutCallingAModelProvider()
    {
        var store = new RecordingCorpusStore();
        using var provider = BuildProvider(store);
        using var scope = provider.CreateScope();
        var modelClient = Assert.IsType<ThrowingModelClient>(
            scope.ServiceProvider.GetRequiredService<IAiModelClient>());

        var report = await scope.ServiceProvider
            .GetRequiredService<IApplicationDispatcher>()
            .DispatchAsync<RunRetrievalBaselineCommand, RetrievalBaselineReport>(
                new RunRetrievalBaselineCommand(DatasetVersion: null, CodeRevision: "abc123"),
                TestContext.Current.CancellationToken);

        Assert.Equal("retrieval-baseline-sentinel", report.DatasetVersion);
        Assert.Equal(2, report.Queries.Count);
        Assert.Equal(0, modelClient.CallCount);
    }

    private static ServiceProvider BuildProvider(RecordingCorpusStore? configureStore)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GenAIPlatform:Embeddings:MockVariant"] = "Lexical",
                ["GenAIPlatform:Embeddings:MockDimensions"] = "1024"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAiModelClient>(new ThrowingModelClient());
        if (configureStore is not null)
        {
            services.AddSingleton<IRetrievalBaselineCorpusStore>(configureStore);
            services.AddSingleton<IRagVectorSearchStore>(new CorpusEchoSearchStore(configureStore));
            services.AddSingleton<IRetrievalBaselineDatasetProvider>(
                new StubDatasetProvider(SentinelDataset()));
        }

        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }

    private static RetrievalBaselineDataset SentinelDataset()
    {
        return new RetrievalBaselineDataset(
            "retrieval-baseline-sentinel",
            "retrieval-baseline",
            new RetrievalBaselineGates(0.9, 1, 3),
            [
                new RetrievalBaselineDocument(
                    "doc-sentinel",
                    "retrieval-baseline",
                    "bench-alice",
                    "TenantPublic",
                    1,
                    "Sentinel Notes",
                    "sentinel-notes.md",
                    [new RetrievalBaselineChunk("chunk-sentinel-1", $"{ChunkSentinel} inside a benchmark chunk.")])
            ],
            [
                new RetrievalBaselineQuery(
                    "q-sentinel-relevant",
                    $"{QuestionSentinel} in a benchmark question?",
                    "retrieval-baseline",
                    "bench-alice",
                    RetrievalBaselineCategories.Relevant,
                    ["doc-sentinel"],
                    false,
                    ["chunk-sentinel-1"]),
                new RetrievalBaselineQuery(
                    "q-sentinel-no-match",
                    $"{QuestionSentinel} that nothing answers?",
                    "retrieval-baseline-empty",
                    "bench-alice",
                    RetrievalBaselineCategories.NoMatch,
                    [],
                    true)
            ]);
    }

    private static RetrievalBaselineReport Report(bool gatesPassed)
    {
        return new RetrievalBaselineReport(
            "retrieval-baseline-v1",
            new string('a', 64),
            "abc123",
            DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            new RetrievalBaselineConfiguration(
                "mock-lexical",
                "mock-embedding",
                "Lexical",
                1024,
                3,
                0.2,
                new string('b', 64)),
            new RetrievalBaselineEnvironment("test-os", "test-runtime", "16.4", "0.7.4"),
            new RetrievalBaselineAggregates(
                33,
                21,
                gatesPassed ? 1 : 0,
                gatesPassed ? 1 : 0,
                12,
                1,
                new Dictionary<string, int> { ["relevant"] = 8 },
                new Dictionary<string, int> { ["relevant"] = gatesPassed ? 8 : 0 }),
            [
                new RetrievalBaselineGateResult(
                    RetrievalBaselineGateResult.RecallGateName,
                    0.9,
                    gatesPassed ? 1 : 0,
                    gatesPassed),
                new RetrievalBaselineGateResult(
                    RetrievalBaselineGateResult.NoMatchGateName,
                    1,
                    1,
                    true)
            ],
            gatesPassed,
            [
                new RetrievalBaselineQueryReport(
                    "q-relevant-001",
                    "relevant",
                    false,
                    ["doc-photosynthesis"],
                    gatesPassed ? ["doc-photosynthesis"] : [],
                    gatesPassed ? 1 : 0,
                    gatesPassed ? 1 : null,
                    gatesPassed,
                    1.5)
            ],
            new RetrievalBaselineTimings(120, 40, 2.5, 9));
    }

    /// <summary>
    /// Accepts the built corpus in memory so a sanitization run needs no database, and
    /// keeps it so the search double can return the very chunk text the run embedded.
    /// </summary>
    private sealed class RecordingCorpusStore : IRetrievalBaselineCorpusStore
    {
        public RetrievalBaselineCorpus? Corpus { get; private set; }

        public Task ReplaceCorpusAsync(RetrievalBaselineCorpus corpus, CancellationToken cancellationToken)
        {
            Corpus = corpus;
            return Task.CompletedTask;
        }

        public Task<RetrievalBaselineEnvironment> GetEnvironmentAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new RetrievalBaselineEnvironment("test-os", "test-runtime", "16.4", "0.8.2"));
        }
    }

    /// <summary>
    /// Returns the stored benchmark chunks of the query's own tenant, so retrieved chunk
    /// text really does pass through the executor on its way to the report.
    /// </summary>
    private sealed class CorpusEchoSearchStore(RecordingCorpusStore store) : IRagVectorSearchStore
    {
        public Task CheckReadinessAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
            RagVectorSearchQuery query,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<RetrievedDocumentChunk> chunks =
            [
                .. (store.Corpus?.Documents ?? [])
                    .Where(document => string.Equals(document.TenantId, query.TenantId, StringComparison.Ordinal))
                    .SelectMany(document => document.Chunks.Select(chunk => new RetrievedDocumentChunk(
                        document.DocumentId,
                        chunk.ChunkId,
                        chunk.DocumentVersion,
                        chunk.Position,
                        document.Title,
                        document.FileName,
                        chunk.Text,
                        1)))
                    .Take(query.TopK)
            ];

            return Task.FromResult(chunks);
        }
    }

    /// <summary>
    /// Fails any completion attempt. The retrieval baseline performs no model call, so a
    /// run that reaches this double is a defect rather than a slow test.
    /// </summary>
    private sealed class ThrowingModelClient : IAiModelClient
    {
        public int CallCount { get; private set; }

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("The retrieval baseline must not call a model provider.");
        }
    }

    private sealed class StubDatasetProvider(RetrievalBaselineDataset dataset) : IRetrievalBaselineDatasetProvider
    {
        public Task<RetrievalBaselineDatasetSource> GetDatasetAsync(
            string? datasetVersion,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new RetrievalBaselineDatasetSource(dataset, new string('c', 64)));
        }
    }
}
