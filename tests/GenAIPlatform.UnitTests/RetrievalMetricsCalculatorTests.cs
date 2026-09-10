using GenAIPlatform.Domain.Evaluations.Retrieval;

namespace GenAIPlatform.UnitTests;

public sealed class RetrievalMetricsCalculatorTests
{
    [Fact]
    public void Score_CountsDuplicateRetrievedDocumentsOnce()
    {
        var result = RetrievalMetricsCalculator.Score(
            Query("q-1", [RetrievalBaselineCategories.Relevant], ["doc-a"]),
            ["doc-b", "doc-b", "doc-a", "doc-a"]);

        Assert.Equal(["doc-b", "doc-a"], result.RetrievedDocumentIds);
        Assert.Equal(2, result.FirstRelevantRank);
        Assert.Equal(0.5, result.ReciprocalRank);
        Assert.Equal(1, result.RecallAtK);
        Assert.True(result.Hit);
    }

    [Fact]
    public void Score_CountsDuplicateExpectedDocumentsOnceInTheRecallDenominator()
    {
        var result = RetrievalMetricsCalculator.Score(
            Query("q-1", [RetrievalBaselineCategories.Relevant], ["doc-a", "doc-a"]),
            ["doc-a"]);

        Assert.Equal(["doc-a"], result.ExpectedDocumentIds);
        Assert.Equal(1, result.RecallAtK);
    }

    [Fact]
    public void Score_ReportsPartialRecallWhenRetrievalDepthIsSmallerThanTheRelevantSet()
    {
        var result = RetrievalMetricsCalculator.Score(
            Query("q-1", [RetrievalBaselineCategories.Relevant], ["doc-a", "doc-b", "doc-c"]),
            ["doc-c"]);

        Assert.Equal(1d / 3d, result.RecallAtK);
        Assert.Equal(1, result.FirstRelevantRank);
        Assert.True(result.Hit);
    }

    [Fact]
    public void Score_ReportsNoRankWhenNothingRelevantWasRetrieved()
    {
        var result = RetrievalMetricsCalculator.Score(
            Query("q-1", [RetrievalBaselineCategories.Distractor], ["doc-a"]),
            ["doc-b", "doc-c"]);

        Assert.Equal(0, result.RecallAtK);
        Assert.Null(result.FirstRelevantRank);
        Assert.Equal(0, result.ReciprocalRank);
        Assert.False(result.Hit);
    }

    [Fact]
    public void Score_ExcludesQueriesWithoutRelevantDocumentsFromRecallAndRank()
    {
        var empty = RetrievalMetricsCalculator.Score(
            Query("q-1", [RetrievalBaselineCategories.NoMatch], [], noMatch: true),
            []);
        var leaked = RetrievalMetricsCalculator.Score(
            Query("q-2", [RetrievalBaselineCategories.NoMatch], [], noMatch: true),
            ["doc-a"]);

        Assert.Null(empty.RecallAtK);
        Assert.Null(empty.FirstRelevantRank);
        Assert.Equal(0, empty.ReciprocalRank);
        Assert.True(empty.Hit);
        Assert.Null(leaked.RecallAtK);
        Assert.False(leaked.Hit);
    }

    [Fact]
    public void Aggregate_AveragesOverRecallEligibleQueriesOnly()
    {
        var metrics = RetrievalMetricsCalculator.Aggregate(
        [
            RetrievalMetricsCalculator.Score(
                Query("q-1", [RetrievalBaselineCategories.Relevant], ["doc-a"]),
                ["doc-a"]),
            RetrievalMetricsCalculator.Score(
                Query("q-2", [RetrievalBaselineCategories.Relevant], ["doc-b"]),
                ["doc-c", "doc-b"]),
            RetrievalMetricsCalculator.Score(
                Query("q-3", [RetrievalBaselineCategories.NoMatch], [], noMatch: true),
                [])
        ]);

        Assert.Equal(3, metrics.QueryCount);
        Assert.Equal(2, metrics.RecallEligibleQueryCount);
        Assert.Equal(1, metrics.MeanRecallAtK);
        Assert.Equal(0.75, metrics.MeanReciprocalRank);
        Assert.Equal(1, metrics.NoMatchQueryCount);
        Assert.Equal(1, metrics.NoMatchAccuracy);
    }

    [Fact]
    public void Aggregate_ReportsNoMatchAccuracyAsTheShareOfEmptyResultSets()
    {
        var metrics = RetrievalMetricsCalculator.Aggregate(
        [
            RetrievalMetricsCalculator.Score(
                Query("q-1", [RetrievalBaselineCategories.NoMatch], [], noMatch: true),
                []),
            RetrievalMetricsCalculator.Score(
                Query("q-2", [RetrievalBaselineCategories.TenantIsolation], [], noMatch: true),
                ["doc-a"]),
            RetrievalMetricsCalculator.Score(
                Query("q-3", [RetrievalBaselineCategories.PrivateOwnership], [], noMatch: true),
                []),
            RetrievalMetricsCalculator.Score(
                Query("q-4", [RetrievalBaselineCategories.EmbeddingCompatibility], [], noMatch: true),
                [])
        ]);

        Assert.Equal(4, metrics.NoMatchQueryCount);
        Assert.Equal(0.75, metrics.NoMatchAccuracy);
        Assert.Equal(0, metrics.RecallEligibleQueryCount);
        Assert.Equal(0, metrics.MeanRecallAtK);
        Assert.Equal(0, metrics.MeanReciprocalRank);
    }

    [Fact]
    public void Aggregate_ReportsFullNoMatchAccuracyWhenNoQueryIsLabeledNoMatch()
    {
        var metrics = RetrievalMetricsCalculator.Aggregate(
        [
            RetrievalMetricsCalculator.Score(
                Query("q-1", [RetrievalBaselineCategories.Relevant], ["doc-a"]),
                ["doc-a"])
        ]);

        Assert.Equal(0, metrics.NoMatchQueryCount);
        Assert.Equal(1, metrics.NoMatchAccuracy);
    }

    [Fact]
    public void Aggregate_CountsQueriesAndHitsPerCategory()
    {
        var metrics = RetrievalMetricsCalculator.Aggregate(
        [
            RetrievalMetricsCalculator.Score(
                Query("q-1", [RetrievalBaselineCategories.Relevant], ["doc-a"]),
                ["doc-a"]),
            RetrievalMetricsCalculator.Score(
                Query("q-2", [RetrievalBaselineCategories.Relevant], ["doc-b"]),
                ["doc-c"]),
            RetrievalMetricsCalculator.Score(
                Query("q-3", [RetrievalBaselineCategories.NoMatch], [], noMatch: true),
                [])
        ]);

        Assert.Equal(2, metrics.QueryCountsByCategory[RetrievalBaselineCategories.Relevant]);
        Assert.Equal(1, metrics.HitCountsByCategory[RetrievalBaselineCategories.Relevant]);
        Assert.Equal(1, metrics.QueryCountsByCategory[RetrievalBaselineCategories.NoMatch]);
        Assert.Equal(1, metrics.HitCountsByCategory[RetrievalBaselineCategories.NoMatch]);
    }

    [Fact]
    public void Aggregate_ReportsEmptyRunWithoutDividingByZero()
    {
        var metrics = RetrievalMetricsCalculator.Aggregate([]);

        Assert.Equal(0, metrics.QueryCount);
        Assert.Equal(0, metrics.RecallEligibleQueryCount);
        Assert.Equal(0, metrics.MeanRecallAtK);
        Assert.Equal(0, metrics.MeanReciprocalRank);
        Assert.Equal(1, metrics.NoMatchAccuracy);
        Assert.Empty(metrics.QueryCountsByCategory);
    }

    private static RetrievalBaselineQuery Query(
        string id,
        IReadOnlyList<string> categories,
        IReadOnlyList<string> expectedDocumentIds,
        bool noMatch = false)
    {
        return new RetrievalBaselineQuery(
            id,
            "synthetic question",
            "retrieval-baseline",
            "bench-alice",
            categories[0],
            expectedDocumentIds,
            noMatch);
    }
}
