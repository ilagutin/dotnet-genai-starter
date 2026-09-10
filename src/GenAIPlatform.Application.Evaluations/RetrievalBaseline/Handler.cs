using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;
using GenAIPlatform.Application.Generation.Chat;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Domain.Evaluations.Retrieval;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// Runs the frozen retrieval baseline: validate the dataset, replace the isolated
/// benchmark corpus, retrieve every labeled query through the production retrieval port
/// and score the run against the dataset gates. No model completion is performed.
/// </summary>
internal sealed class RunRetrievalBaselineHandler(
    IRetrievalBaselineDatasetProvider datasetProvider,
    RetrievalBaselineDatasetValidator datasetValidator,
    RetrievalBaselineCorpusBuilder corpusBuilder,
    IRetrievalBaselineCorpusStore corpusStore,
    RetrievalBaselineQueryExecutor queryExecutor,
    RetrievalBaselineGateEvaluator gateEvaluator,
    IOptions<EmbeddingOptions> embeddingOptions,
    IOptions<RagOptions> ragOptions,
    TimeProvider timeProvider)
    : IRequestHandler<RunRetrievalBaselineCommand, RetrievalBaselineReport>
{
    private const string UnknownRevision = "unknown";

    public async Task<RetrievalBaselineReport> HandleAsync(
        RunRetrievalBaselineCommand request,
        CancellationToken cancellationToken)
    {
        var runStarted = timeProvider.GetTimestamp();
        var source = await datasetProvider.GetDatasetAsync(request.DatasetVersion, cancellationToken);
        var dataset = datasetValidator.Validate(source.Dataset);

        var build = await corpusBuilder.BuildAsync(dataset, cancellationToken);
        var replaceStarted = timeProvider.GetTimestamp();
        await corpusStore.ReplaceCorpusAsync(build.Corpus, cancellationToken);
        var replaceElapsed = timeProvider.GetElapsedTime(replaceStarted);

        var settings = new RetrievalBaselineSearchSettings(
            build.EmbeddingModel,
            dataset.Gates.K,
            ragOptions.Value.DefaultMinSimilarityScore);
        var (results, queryReports) = await RunQueriesAsync(dataset, settings, build, cancellationToken);
        var metrics = RetrievalMetricsCalculator.Aggregate(results);
        var gates = gateEvaluator.Evaluate(dataset.Gates, metrics);

        return new RetrievalBaselineReport(
            dataset.Version,
            source.ContentHash,
            NormalizeRevision(request.CodeRevision),
            timeProvider.GetUtcNow(),
            CreateConfiguration(build, settings),
            await corpusStore.GetEnvironmentAsync(cancellationToken),
            CreateAggregates(metrics),
            gates,
            gates.All(static gate => gate.Met),
            queryReports,
            CreateTimings(runStarted, replaceElapsed, queryReports));
    }

    private async Task<(IReadOnlyList<RetrievalBaselineQueryResult> Results, IReadOnlyList<RetrievalBaselineQueryReport> Reports)> RunQueriesAsync(
        RetrievalBaselineDataset dataset,
        RetrievalBaselineSearchSettings settings,
        RetrievalBaselineCorpusBuildResult build,
        CancellationToken cancellationToken)
    {
        var results = new List<RetrievalBaselineQueryResult>(dataset.Queries.Count);
        var reports = new List<RetrievalBaselineQueryReport>(dataset.Queries.Count);

        foreach (var query in dataset.Queries)
        {
            var execution = await queryExecutor.ExecuteAsync(
                query,
                settings,
                build.DocumentIdsByRowId,
                cancellationToken);
            var result = RetrievalMetricsCalculator.Score(query, execution.RetrievedDocumentIds);
            results.Add(result);
            reports.Add(new RetrievalBaselineQueryReport(
                result.QueryId,
                result.Category,
                result.NoMatch,
                result.ExpectedDocumentIds,
                result.RetrievedDocumentIds,
                result.RecallAtK,
                result.FirstRelevantRank,
                result.Hit,
                execution.ElapsedMilliseconds));
        }

        return (results, reports);
    }

    private RetrievalBaselineConfiguration CreateConfiguration(
        RetrievalBaselineCorpusBuildResult build,
        RetrievalBaselineSearchSettings settings)
    {
        // The canonical variant, not the configured spelling, is what the run measured
        // under, so two runs configured "lexical" and "Lexical" share one settings hash.
        var mockVariant = MockEmbeddingVariantCanonicalizer.Canonicalize(embeddingOptions.Value.MockVariant);

        return new RetrievalBaselineConfiguration(
            build.EmbeddingProvider,
            build.EmbeddingModel,
            mockVariant,
            build.EmbeddingDimensions,
            settings.TopK,
            settings.MinSimilarityScore,
            RetrievalBaselineSettingsHasher.Hash(
                build.EmbeddingProvider,
                build.EmbeddingModel,
                mockVariant,
                build.EmbeddingDimensions,
                settings.TopK,
                settings.MinSimilarityScore));
    }

    private static RetrievalBaselineAggregates CreateAggregates(RetrievalMetrics metrics)
    {
        return new RetrievalBaselineAggregates(
            metrics.QueryCount,
            metrics.RecallEligibleQueryCount,
            metrics.MeanRecallAtK,
            metrics.MeanReciprocalRank,
            metrics.NoMatchQueryCount,
            metrics.NoMatchAccuracy,
            metrics.QueryCountsByCategory,
            metrics.HitCountsByCategory);
    }

    private RetrievalBaselineTimings CreateTimings(
        long runStarted,
        TimeSpan replaceElapsed,
        IReadOnlyList<RetrievalBaselineQueryReport> reports)
    {
        return new RetrievalBaselineTimings(
            timeProvider.GetElapsedTime(runStarted).TotalMilliseconds,
            replaceElapsed.TotalMilliseconds,
            reports.Count == 0 ? 0 : reports.Average(static report => report.ElapsedMilliseconds),
            reports.Count == 0 ? 0 : reports.Max(static report => report.ElapsedMilliseconds));
    }

    private static string NormalizeRevision(string? codeRevision)
    {
        return string.IsNullOrWhiteSpace(codeRevision) ? UnknownRevision : codeRevision.Trim();
    }
}
